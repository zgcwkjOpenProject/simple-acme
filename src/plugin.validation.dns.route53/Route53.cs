﻿using Amazon;
using Amazon.Route53;
using Amazon.Route53.Model;
using Amazon.Runtime;
using PKISharp.WACS.Clients.DNS;
using PKISharp.WACS.Plugins.Base.Capabilities;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Dns
{
    [IPlugin.Plugin1<
        Route53Options, Route53OptionsFactory, 
        DnsValidationCapability, Route53Json, Route53Arguments>
        ("4e5dc595-45c7-4461-929a-8f96a0c96b3d", 
        "Route53", "Create verification records in Route 53 DNS", 
        Name = "Route 53", External = true, Provider = "Amazon AWS")]
    internal sealed class Route53 : DnsValidation<Route53>
    {
        private readonly AmazonRoute53Client _route53Client;
        private readonly Dictionary<string, List<ResourceRecordSet>> _pendingZoneUpdates = [];

        public override ParallelOperations Parallelism => ParallelOperations.Answer; 
        public Route53(
            LookupClientProvider dnsClient,
            ILogService log,
            IProxyService proxy,
            ISettingsService settings,
            SecretServiceManager ssm,
            Route53Options options) : base(dnsClient, log, settings)
        {
            var region = RegionEndpoint.USEast1;
            var config = new AmazonRoute53Config() { RegionEndpoint = region };
            config.SetWebProxy(proxy.GetWebProxy());
            _route53Client = !string.IsNullOrWhiteSpace(options.IAMRole)
                ? new AmazonRoute53Client(new InstanceProfileAWSCredentials(options.IAMRole), config)
                : !string.IsNullOrWhiteSpace(options.AccessKeyId)
                    ? new AmazonRoute53Client(options.AccessKeyId, ssm.EvaluateSecret(options.SecretAccessKey), config)
                    : new AmazonRoute53Client(config);
        }

        private void CreateOrUpdateResourceRecordSet(string hostedZone, string name, string record)
        {
            lock (_pendingZoneUpdates)
            {
                if (!_pendingZoneUpdates.TryGetValue(hostedZone, out List<ResourceRecordSet>? value))
                {
                    value = [];
                    _pendingZoneUpdates.Add(hostedZone, value);
                }
                var pendingRecordSets = value;
                var existing = pendingRecordSets.FirstOrDefault(x => x.Name == name);
                if (existing == null)
                {
                    existing = new ResourceRecordSet
                    {
                        Name = name,
                        Type = RRType.TXT,
                        ResourceRecords = [],
                        TTL = 1L
                    };
                    pendingRecordSets.Add(existing);
                }
                var formattedValue = $"\"{record}\"";
                if (!existing.ResourceRecords.Any(x => x.Value == formattedValue))
                {
                    existing.ResourceRecords.Add(new ResourceRecord(formattedValue));
                }
            }
        }

        /// <summary>
        /// Only create a list of which sets we are going to create in each zone.
        /// Changes are only submitted in the SaveChanges phase.
        /// </summary>
        /// <param name="record"></param>
        /// <returns></returns>
        public override async Task<bool> CreateRecord(DnsValidationRecord record)
        {
            try
            {
                var recordName = record.Authority.Domain;
                var token = record.Value;
                var hostedZoneIds = await GetHostedZoneIds(recordName);
                if (hostedZoneIds == null)
                {
                    return false;
                }
                _log.Information("Creating TXT record {recordName} with value {token}", recordName, token);
                foreach (var zone in hostedZoneIds)
                {
                    CreateOrUpdateResourceRecordSet(zone, recordName, token);
                }
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning(ex, $"Error creating TXT record");
                return false;
            }
        }

        /// <summary>
        /// Wait for propageation
        /// </summary>
        /// <returns></returns>
        public override async Task SaveChanges()
        {
            var updateTasks = new List<Task<ChangeResourceRecordSetsResponse>>();
            foreach (var zone in _pendingZoneUpdates.Keys)
            {
                var recordSets = _pendingZoneUpdates[zone];
                updateTasks.Add(_route53Client.ChangeResourceRecordSetsAsync(
                    new ChangeResourceRecordSetsRequest(
                        zone,
                        new ChangeBatch(recordSets.Select(x => new Change(ChangeAction.UPSERT, x)).ToList()))));
            }

            var results = await Task.WhenAll(updateTasks);
            var pendingChanges = results.Select(result => result.ChangeInfo);
            var propagationTasks = pendingChanges.Select(change => WaitChangesPropagation(change));
            await Task.WhenAll(propagationTasks);
        }

        /// <summary>
        /// Delete created records, do not wait for propagation here
        /// </summary>
        /// <returns></returns>
        public override async Task Finalize()
        {
            var deleteTasks = new List<Task<ChangeResourceRecordSetsResponse>>();
            foreach (var zone in _pendingZoneUpdates.Keys)
            {
                var recordSets = _pendingZoneUpdates[zone];
                deleteTasks.Add(_route53Client.ChangeResourceRecordSetsAsync(
                    new ChangeResourceRecordSetsRequest(
                        zone,
                        new ChangeBatch(recordSets.Select(x => new Change(ChangeAction.DELETE, x)).ToList()))));
            }
            _ = await Task.WhenAll(deleteTasks);
        }      

        /// <summary>
        /// Find matching hosted zones
        /// </summary>
        /// <param name="recordName"></param>
        /// <returns></returns>
        private async Task<IEnumerable<string>?> GetHostedZoneIds(string recordName)
        {
            var hostedZones = new List<HostedZone>();
            var response = await _route53Client.ListHostedZonesAsync();
            hostedZones.AddRange(response.HostedZones);
            while (response.IsTruncated)
            {
                response = await _route53Client.ListHostedZonesAsync(
                    new ListHostedZonesRequest() {
                        Marker = response.NextMarker
                    });
                hostedZones.AddRange(response.HostedZones);
            }
            _log.Debug("Found {count} hosted zones in AWS", hostedZones.Count);

            hostedZones = hostedZones.Where(x => !x.Config.PrivateZone).ToList();
            var hostedZoneSets = hostedZones.GroupBy(x => x.Name);
            var hostedZone = FindBestMatch(hostedZoneSets.ToDictionary(x => x.Key), recordName);
            if (hostedZone != null)
            {
                return hostedZone.Select(x => x.Id);
            }
            _log.Error($"Can't find hosted zone for domain {recordName}");
            return null;
        }

        private async Task WaitChangesPropagation(ChangeInfo changeInfo)
        {
            if (changeInfo.Status == ChangeStatus.INSYNC)
            {
                return;
            }

            _log.Information("Waiting for DNS changes propagation");

            var changeRequest = new GetChangeRequest(changeInfo.Id);

            while ((await _route53Client.GetChangeAsync(changeRequest)).ChangeInfo.Status == ChangeStatus.PENDING)
            {
                await Task.Delay(2000);
            }
        }
    }
}