using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace UploadSupplyChainAudioTranscriptions.Services
{
    public class AzureTableService
    {
        private readonly TableServiceClient _serviceClient;
        private readonly string _connectionString;

        public AzureTableService(string connectionString)
        {
            _connectionString = connectionString;
            _serviceClient = new TableServiceClient(_connectionString);
        }

        public TableClient GetTableClient(string tableName)
        {
            var tableClient = _serviceClient.GetTableClient(tableName);
            tableClient.CreateIfNotExists();
            return tableClient;
        }

        public async Task AddEntityAsync<T>(string tableName, T entity) where T : class, ITableEntity, new()
        {
            var tableClient = GetTableClient(tableName);
            await tableClient.AddEntityAsync(entity);
        }

        public async Task AddEntitiesAsync<T>(string tableName, List<T> entities) where T : class, ITableEntity, new()
        {
            var tableClient = GetTableClient(tableName);

            // Azure Table Storage batch limit is 100 entities per batch and all must share the same PartitionKey
            // Group entities by PartitionKey
            foreach (var partitionGroup in entities.GroupBy(e => e.PartitionKey))
            {
                var batch = new List<TableTransactionAction>();
                foreach (var entity in partitionGroup)
                {
                    batch.Add(new TableTransactionAction(TableTransactionActionType.Add, entity));
                    // If batch reaches 100, execute and start a new batch
                    if (batch.Count == 100)
                    {
                        await tableClient.SubmitTransactionAsync(batch);
                        batch.Clear();
                    }
                }
                // Submit any remaining entities in the batch
                if (batch.Count > 0)
                {
                    await tableClient.SubmitTransactionAsync(batch);
                }
            }
        }

        public async Task<T?> GetEntityAsync<T>(string tableName, string partitionKey, string rowKey) where T : class, ITableEntity, new()
        {
            var tableClient = GetTableClient(tableName);
            try
            {
                var response = await tableClient.GetEntityAsync<T>(partitionKey, rowKey);
                return response.Value;
            }
            catch (RequestFailedException)
            {
                return null;
            }
        }

        public async Task UpdateEntityAsync<T>(string tableName, T entity) where T : class, ITableEntity, new()
        {
            var tableClient = GetTableClient(tableName);
            await tableClient.UpdateEntityAsync(entity, entity.ETag, TableUpdateMode.Replace);
        }

        public async Task DeleteEntityAsync(string tableName, string partitionKey, string rowKey)
        {
            var tableClient = GetTableClient(tableName);
            await tableClient.DeleteEntityAsync(partitionKey, rowKey);
        }

        public async Task<List<T>> QueryEntitiesAsync<T>(string tableName, string filter = null) where T : class, ITableEntity, new()
        {
            var tableClient = GetTableClient(tableName);
            var entities = new List<T>();
            await foreach (var entity in tableClient.QueryAsync<T>(filter))
            {
                entities.Add(entity);
            }
            return entities;
        }
    }
}
