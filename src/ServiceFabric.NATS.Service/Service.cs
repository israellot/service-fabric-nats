using Microsoft.ServiceFabric.Services.Client;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Description;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace ServiceFabric.NATS
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class Service : StatelessService
    {
      

        public Service(StatelessServiceContext context)
            : base(context)
        {


        }

        /// <summary>
        /// Optional override to create listeners (like tcp, http) for this service instance.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            EndpointResourceDescription natsServerEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_SERVER_ENDPOINT");
            EndpointResourceDescription natsClusterEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_CLUSTER_ENDPOINT");
            EndpointResourceDescription natsMonitorEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_HTTP_MONITOR_ENDPOINT");
                      
            return new ServiceInstanceListener[]
            {
                new ServiceInstanceListener(serviceContext => new NatsListener(natsServerEndpoint.IpAddressOrFqdn,natsServerEndpoint.Port),"NATS_Server"),
                new ServiceInstanceListener(serviceContext => new NatsListener(natsClusterEndpoint.IpAddressOrFqdn,natsClusterEndpoint.Port),"NATS_Cluster"),
                new ServiceInstanceListener(serviceContext => new NatsListener(natsMonitorEndpoint.IpAddressOrFqdn,natsMonitorEndpoint.Port),"NATS_Monitor")

            };
        }

        protected override Task OnOpenAsync(CancellationToken cancellationToken)
        {
            return base.OnOpenAsync(cancellationToken);
        }

        protected override async Task RunAsync(CancellationToken cancellationToken)
        {

    start:

            //get template config file
            var template = File.ReadAllText("nats.template.conf");

            //read endpoints
            EndpointResourceDescription natsServerEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_SERVER_ENDPOINT");
            EndpointResourceDescription natsClusterEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_CLUSTER_ENDPOINT");
            EndpointResourceDescription natsMonitorEndpoint = Context.CodePackageActivationContext.GetEndpoint("NATS_HTTP_MONITOR_ENDPOINT");

            //read configs
            var configurationPackage = Context.CodePackageActivationContext.GetConfigurationPackageObject("Config");
            var natsSection = configurationPackage.Settings.Sections["NATS"];

            var serverAuthUser = natsSection.Parameters["NATS_SERVER_AUTH_USER"].Value;
            var serverAuthPassword = natsSection.Parameters["NATS_SERVER_AUTH_PASSWORD"].Value;

            var clusterUser = natsSection.Parameters["NATS_CLUSTER_ROUTE_USER"].Value;
            var clusterPassword = natsSection.Parameters["NATS_CLUSTER_ROUTE_PASSWORD"].Value;

            //replace endpoints in template
            template = template.Replace("NATS_SERVER_PORT", natsServerEndpoint.Port.ToString());
            template = template.Replace("NATS_CLUSTER_PORT", natsClusterEndpoint.Port.ToString());
            template = template.Replace("NATS_HTTP_MOINTOR_PORT", natsMonitorEndpoint.Port.ToString());
#if DEBUG
            template = template.Replace("NATS_SERVER_HOST", "127.0.0.1");
#else
            template = template.Replace("NATS_SERVER_HOST", Context.NodeContext.IPAddressOrFQDN);
#endif

            //replace server auth
            template = template.Replace("NATS_SERVER_AUTH_USER", serverAuthUser);
            template = template.Replace("NATS_SERVER_AUTH_PASSWORD", serverAuthPassword);

            //replace cluster auth
            template = template.Replace("NATS_CLUSTER_ROUTE_USER", clusterUser);
            template = template.Replace("NATS_CLUSTER_ROUTE_PASSWORD", clusterPassword);

            //find NATS peers
            string ownAddress;
#if DEBUG
            ownAddress = $"127.0.0.1:{natsClusterEndpoint.Port}";
#else
            ownAddress =  $"{Context.NodeContext.IPAddressOrFQDN}:{natsClusterEndpoint.Port}";
#endif
            var peers = await ResolveNatsPeers();
            peers = peers.Except(new[] { ownAddress }).ToList();

            //configure route to peers
            if (peers != null && peers.Count() > 0)
            {
                var routes = peers.Select(p => $"nats-routes://{clusterUser}:{clusterPassword}@{p}");

                var sb = new StringBuilder();

                sb.AppendLine("routes = [");

                foreach (var r in routes)
                    sb.AppendLine(r);

                sb.AppendLine("]");

                //processStart.Arguments += $" -routes {string.Join(',', routes)}";

                template = template.Replace("NATS_CLUSTER_ROUTES", sb.ToString());
            }
            else
            {
                template = template.Replace("NATS_CLUSTER_ROUTES",string.Empty);
            }

            //get replica temp folder
            var tempFolder = Context.CodePackageActivationContext.TempDirectory;


            var replicaId = Context.ReplicaOrInstanceId;

#if !DEBUG
            //replace log file
            var logFile = Path.Combine(tempFolder, $"{replicaId}.nats.log");
            template = template.Replace("NATS_LOG_FILE", logFile.Replace(@"\", @"\\"));
#endif

            //write config file to temp folder
            var configFilePath = Path.Combine(tempFolder, $"{replicaId}.nats.conf");
            File.WriteAllText(configFilePath, template);



            //start nats process
            var processStart = new ProcessStartInfo("gnatsd.exe");
            processStart.EnvironmentVariables.Add("NATS_DOCKERIZED", "1");
            processStart.Arguments = $"-c {configFilePath}";
            processStart.UseShellExecute = false;
            //processStart.CreateNoWindow = true;
            processStart.RedirectStandardOutput = true;
            processStart.RedirectStandardInput = true;
            processStart.WorkingDirectory = Directory.GetCurrentDirectory();

            //set log
            var logFile = Path.Combine(tempFolder, $"{replicaId}.nats.log");
            processStart.Arguments += $" -l {logFile}";


            var process = new Process()
            {
                StartInfo = processStart
            };

            process.Start();

#if DEBUG
            //redirect console to debug
            _=Task.Run(async () => {

                //open shared stream to log file
                FileStream fs = new FileStream(logFile, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                StreamReader sr = new StreamReader(fs); 
                
                while (!cancellationToken.IsCancellationRequested && process != null)
                {
                    string line = await sr.ReadLineAsync();
                    if (!string.IsNullOrEmpty(line))
                        Debug.WriteLine($"[nats] [{replicaId}] : {line}");
                    else
                        await Task.Delay(1000);
                }

            });
#endif

            while (!cancellationToken.IsCancellationRequested)
            {
                if (process.HasExited)
                {
                    //nats process exit, start again
                    goto start;
                }

                try
                {
                    await Task.Delay(1000,cancellationToken);
                }catch(TaskCanceledException ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        //runtime requests us to stop

                        break;
                    }
                }
            }

            //kill nats process
            //force kill it not exited yet
            if (!process.HasExited)
                try { process?.Kill(); process = null; } catch { }
        }

        

        protected async Task<List<string>> ResolveNatsPeers()
        {
            
            var serviceName = new Uri($"{Context.ServiceName}");

            var resolver = ServicePartitionResolver.GetDefault();

            var resolved = await resolver.ResolveAsync(serviceName, ServicePartitionKey.Singleton, CancellationToken.None);

            List<string> peers = new List<string>();
            foreach (var endpoint in resolved.Endpoints)
            {
                var json = (dynamic)JsonConvert.DeserializeObject(endpoint.Address);
                var address = (string)json.Endpoints["NATS_Cluster"].ToString(); //get cluster remote endpoint

                peers.Add(address);
            }

            return peers;
        }

      
    }
}
