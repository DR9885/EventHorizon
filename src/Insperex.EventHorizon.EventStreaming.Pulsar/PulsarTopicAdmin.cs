﻿using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Insperex.EventHorizon.EventStreaming.Interfaces.Streaming;
using Insperex.EventHorizon.EventStreaming.Pulsar.Generated;
using Insperex.EventHorizon.EventStreaming.Pulsar.Models;
using Insperex.EventHorizon.EventStreaming.Pulsar.Utils;
using Microsoft.Extensions.Options;

namespace Insperex.EventHorizon.EventStreaming.Pulsar;

public class PulsarTopicAdmin : ITopicAdmin
{
    private readonly ClustersBaseClient _clustersBaseClient;
    private readonly TenantsBaseClient _tenantsBaseClient;
    private readonly NamespacesClient _namespacesClient;
    private readonly PersistentTopicsClient _persistentTopicsClient;
    private readonly NonPersistentTopicsClient _nonPersistentTopicsClient;

    public PulsarTopicAdmin(IOptions<PulsarConfig> options)
    {
        var baseUrl = $"{options.Value.AdminUrl}/admin/v2/";
        var httpClient = new HttpClient();
        _clustersBaseClient = new ClustersBaseClient(baseUrl, httpClient);
        _tenantsBaseClient = new TenantsBaseClient(baseUrl, httpClient);
        _namespacesClient = new NamespacesClient(baseUrl, httpClient);
        _persistentTopicsClient = new PersistentTopicsClient(baseUrl, httpClient);
        _nonPersistentTopicsClient = new NonPersistentTopicsClient(baseUrl, httpClient);
    }

    public async Task RequireTopicAsync(string str, CancellationToken ct)
    {
        var topic = PulsarTopicParser.Parse(str);
        await RequireNamespace(topic.Tenant, topic.Namespace, -1, -1, ct);

        // try
        // {
        //     await _admin.CreateNonPartitionedTopic2Async(topic.Tenant, topic.Namespace, topic.Topic, true, new Dictionary<string, string>(), ct);
        // }
        // catch (ApiException ex)
        // {
        //     // 409 - Partitioned topic already exist
        //     if (ex.StatusCode != 409) 
        //         throw;
        // }
    }

    public async Task DeleteTopicAsync(string str, CancellationToken ct)
    {
        var topic = PulsarTopicParser.Parse(str);
        try
        {
            if (topic.IsPersisted)
                await _persistentTopicsClient.DeleteTopic2Async(topic.Tenant, topic.Namespace, topic.Topic, true, true, ct);
            else
                await _nonPersistentTopicsClient.UnloadTopicAsync(topic.Tenant, topic.Namespace, topic.Topic, true, ct);
        }
        catch (ApiException ex)
        {
            // 404 - ApiException
            if (ex.StatusCode != 409) 
                throw;
        }
    }

    private async Task RequireNamespace(string tenant, string nameSpace, int? retentionInMb, int? retentionInMinutes, CancellationToken ct)
    {
        // Ensure Tenant Exists
        var tenants = await _tenantsBaseClient.GetTenantsAsync(ct);
        if (!tenants.Contains(tenant))
        {
            var clusters = await _clustersBaseClient.GetClustersAsync(ct);
            var tenantInfo = new TenantInfo { AdminRoles = null, AllowedClusters = clusters };
            await _tenantsBaseClient.CreateTenantAsync(tenant, tenantInfo, ct);
        }

        // Ensure Namespace Exists
        var namespaces = await _namespacesClient.GetTenantNamespacesAsync(tenant, ct);
        if (!namespaces.Contains($"{tenant}/{nameSpace}"))
        {
            // Add Retention Policy if namespace == Events
            var policies = !nameSpace.Contains(PulsarConstants.Event)
                ? new Policies()
                : new Policies
                {
                    Retention_policies = new RetentionPolicies
                    {
                        RetentionTimeInMinutes = retentionInMb ?? -1,
                        RetentionSizeInMB = retentionInMinutes ?? -1
                    }
                };
            await _namespacesClient.CreateNamespaceAsync(tenant, nameSpace, policies, ct);
        }
    }
}