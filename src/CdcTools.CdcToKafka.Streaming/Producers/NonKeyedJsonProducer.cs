﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CdcTools.CdcReader.Changes;
using Confluent.Kafka;
using Newtonsoft.Json;
using Confluent.Kafka.Serialization;

namespace CdcTools.CdcToKafka.Streaming.Producers
{
    public class NonKeyedJsonProducer : ProducerBase, IKafkaProducer
    {
        private Producer<Null, string> _producer;

        public NonKeyedJsonProducer(string bootstrapServers, string topic)
            : base(topic)
        {
            _config = new Dictionary<string, object>
            {
                { "bootstrap.servers", bootstrapServers },
                { "socket.blocking.max.ms", "1" } // workaround for https://github.com/confluentinc/confluent-kafka-dotnet/issues/501
            };

            _producer = new Producer<Null, string>(_config, null, new StringSerializer(Encoding.UTF8));
        }

        public async Task SendAsync(CancellationToken token, ChangeRecord changeRecord)
        {
            var change = Convert(changeRecord);
            var jsonText = JsonConvert.SerializeObject(change);
            var sent = false;
            while (!sent && !token.IsCancellationRequested)
            {
                var sendResult = await _producer.ProduceAsync(topic: _topic, key: null, val: jsonText, blockIfQueueFull: true);
                if (sendResult.Error.HasError)
                {
                    Console.WriteLine("Could not send: " + sendResult.Error.Reason);
                    await Task.Delay(100);
                }
                else
                    sent = true;
            }
        }

        private bool _disposed;
        public void Dispose()
        {
            if (!_disposed)
            {
                if (_producer != null)
                    _producer.Dispose();

                _disposed = true;
            }
        }
    }
}
