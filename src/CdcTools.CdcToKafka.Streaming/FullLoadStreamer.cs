﻿using CdcTools.CdcToKafka.Streaming.Producers;
using CdcTools.CdcReader;
using CdcTools.CdcReader.Changes;
using CdcTools.CdcReader.Tables;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CdcTools.CdcToKafka.Streaming
{
    public class FullLoadStreamer
    {
        private List<Task> _loadTasks;
        private string _kafkaTopicPrefix;
        private string _kafkaBootstrapServers;
        private string _schemaRegistryUrl;
        private CdcReaderClient _cdcReaderClient;

        public FullLoadStreamer(IConfiguration configuration, CdcReaderClient cdcReaderClient)
        {
            _cdcReaderClient = cdcReaderClient;
            _kafkaTopicPrefix = configuration["TableTopicPrefix"];
            _kafkaBootstrapServers = configuration["KafkaBootstrapServers"];
            _schemaRegistryUrl = configuration["KafkaSchemaRegistryUrl"];

            _loadTasks = new List<Task>();
        }

        public async Task StreamTablesAsync(CancellationToken token,
            string executionId,
            List<string> tables,
            SerializationMode serializationMode,
            bool sendWithKey,
            int batchSize, 
            int printMod)
        {
            foreach (var tableName in tables)
            {
                var tableSchema = await _cdcReaderClient.GetTableSchemaAsync(tableName);
                _loadTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await StreamTableAsync(token,
                            executionId,
                            tableSchema,
                            serializationMode,
                            sendWithKey,
                            batchSize,
                            printMod);
                    }
                    catch(Exception ex)
                    {
                        Console.WriteLine(ex);
                    }
                }));
            }
        }

        public void WaitForCompletion()
        {
            Task.WaitAll(_loadTasks.ToArray());
        }

        public bool HasFinished()
        {
            return _loadTasks.All(x => x.IsCompleted);
        }

        private async Task StreamTableAsync(CancellationToken token,
            string executionId,
            TableSchema tableSchema,
            SerializationMode serializationMode,
            bool sendWithKey,
            int batchSize,
            int printPercentProgressMod)
        {
            string topicName = _kafkaTopicPrefix + tableSchema.TableName.ToLower();
            var rowCount = await _cdcReaderClient.GetRowCountAsync(tableSchema);
            Console.WriteLine($"Table {tableSchema.Schema}.{tableSchema.TableName} has {rowCount} rows to export");
            int progress = 0;

            using (var producer = ProducerFactory.GetProducer(topicName, tableSchema, serializationMode, sendWithKey, _kafkaBootstrapServers, _schemaRegistryUrl))
            {
                long ctr = 0;
                PrimaryKeyValue lastRetrievedKey = null;
                var existingOffset = await _cdcReaderClient.GetLastFullLoadOffsetAsync(executionId, tableSchema.TableName);
                if (existingOffset.Result == CdcReader.State.Result.NoStoredState)
                {
                    Console.WriteLine($"Table {tableSchema.TableName} - No previous stored offset. Starting from first row");
                    var firstBatch = await _cdcReaderClient.GetFirstBatchAsync(tableSchema, batchSize);
                    ctr = await PublishAsync(producer, token, firstBatch, ctr);
                    lastRetrievedKey = firstBatch.LastRowKey;
                    await _cdcReaderClient.StoreFullLoadOffsetAsync(executionId, tableSchema.TableName, firstBatch.LastRowKey);
                }
                else
                {
                    Console.WriteLine($"Table {tableSchema.TableName} - No data to export");
                    lastRetrievedKey = existingOffset.State;
                }

                bool finished = false;
                
                while (!token.IsCancellationRequested && !finished)
                {
                    var changes = new List<RowChange>();

                    var batch = await _cdcReaderClient.GetBatchAsync(tableSchema, lastRetrievedKey, batchSize);
                    ctr = await PublishAsync(producer, token, batch, ctr);
                    
                    int latestProgress = (int)(((double)ctr / (double)rowCount)*100);
                    if(progress != latestProgress && latestProgress % printPercentProgressMod == 0)
                        Console.WriteLine($"Table {tableSchema.Schema}.{tableSchema.TableName} - Progress at {latestProgress}% ({ctr} records)");

                    progress = latestProgress;
                    lastRetrievedKey = batch.LastRowKey;
                    await _cdcReaderClient.StoreFullLoadOffsetAsync(executionId, tableSchema.TableName, lastRetrievedKey);

                    if (!batch.Records.Any() || batch.Records.Count < batchSize)
                        finished = true;
                }

                if (token.IsCancellationRequested)
                    Console.WriteLine($"Table {tableSchema.Schema}.{tableSchema.TableName} - cancelled at progress at {progress}% ({ctr} records)");
                else
                    Console.WriteLine($"Table {tableSchema.Schema}.{tableSchema.TableName} - complete ({ctr} records)");
            }
        }

        private async Task<long> PublishAsync(IKafkaProducer producer, CancellationToken token, FullLoadBatch batch, long ctr)
        {
            foreach (var row in batch.Records)
            {
                var change = new ChangeRecord();
                change.ChangeKey = row.ChangeKey;
                change.ChangeType = ChangeType.INSERT;
                change.LsnStr = ctr.ToString();
                change.SeqValStr = ctr.ToString();
                change.Data = row.Data;

                await producer.SendAsync(token, change);
                ctr++;
            }

            return ctr;
        }
    }
}
