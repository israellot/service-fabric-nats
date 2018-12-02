using Microsoft.ServiceFabric.Services.Communication.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceFabric.NATS
{
    /// <summary>
    /// An empty listener 
    /// </summary>
    public class NatsListener : ICommunicationListener
    {
        public int Port { get; private set; }
       
        public string Host { get; private set; }

        public NatsListener(string host,int port)
        {
            Port = port;
            Host = host;
        }

        public Task CloseAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
        public void Abort()
        {
           
        }

        public async Task<string> OpenAsync(CancellationToken cancellationToken)
        {

            string address;
#if DEBUG
            address = $"127.0.0.1:{Port}";
#else
            address = $"{FabricRuntime.GetNodeContext().IPAddressOrFQDN}:{Port}";
#endif
            return address;
        }

        
    }
}
