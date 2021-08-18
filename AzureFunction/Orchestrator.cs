using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Azure.Management.ContainerInstance.Fluent;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Services.AppAuthentication;


namespace ExtractorOrchestrator
{
    public static class Orchestrator
    {

        [FunctionName("Orchestrator_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
           [HttpTrigger(AuthorizationLevel.Anonymous, "post")]HttpRequestMessage req,
           [DurableClient] IDurableOrchestrationClient starter,
           ILogger log)
        {
            string instanceId = await starter.StartNewAsync<string>("Orchestrator", await req.Content.ReadAsStringAsync());
            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");
            return starter.CreateCheckStatusResponse(req, instanceId);
        }

        [FunctionName("Orchestrator")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] IDurableOrchestrationContext context,
            [DurableClient] IDurableOrchestrationClient orchestrationClient)
        {
            var youtubeURL = context.GetInput<string>(); 
            var outputs = new List<string>();
            //Generating a callback URL that will be called from the ACI to send the Job_Finished external event
            var callBackURL = orchestrationClient.CreateHttpManagementPayload(context.InstanceId).SendEventPostUri.Replace("{eventName}", "Job_Finished");

            var ipAddress = await context.CallActivityAsync<string>("Orchestrator_Create_ACI_Group", new Tuple<string,string,string,string>("youtube",context.InstanceId, callBackURL,youtubeURL));           
            // Wait for ACI to complete the work and call te callback URL
            await context.WaitForExternalEvent("Job_Finished");
            //This activity function deletes the ACI group once its done with its job
            await context.CallActivityAsync<string>("Orchestrator_Delete_ACI_Group", "youtube-"+context.InstanceId);
            return outputs;
        }

        [FunctionName("Orchestrator_Create_ACI_Group")]
        public static string CreateAciGroup([ActivityTrigger] Tuple<string,string,string,string> args, ILogger log)
        {
            // Leverage User Managed Identiy to login to the Azure management API
            AzureCredentialsFactory factory = new AzureCredentialsFactory();
            AzureCredentials msiCred = factory.FromMSI(new MSILoginInformation(MSIResourceType.AppService, clientId: Environment.GetEnvironmentVariable("clientId")), AzureEnvironment.AzureGlobalCloud);
            // The Identity is scoped to the current resource group, thereore we can use the default Subscription
            var azure = Microsoft.Azure.Management.Fluent.Azure.Configure().Authenticate(msiCred).WithDefaultSubscription();
            var containerGroupName = args.Item1 + "-" + args.Item2;
            
            //Create the container group
            return CreateContainerGroup(azure, "ML-Training-RG", containerGroupName, Environment.GetEnvironmentVariable("containerImage"), args.Item3, args.Item4);
        }

        /// <summary>
        /// Creates a container group with a single container.
        /// </summary>
        /// <param name="azure">An authenticated IAzure object.</param>
        /// <param name="resourceGroupName">The name of the resource group in which to create the container group.</param>
        /// <param name="containerGroupName">The name of the container group to create.</param>
        /// <param name="containerImage">The container image name and tag, for example 'microsoft\aci-helloworld:latest'.</param>
        /// <param name="callbackURL"></param> The call back URL that will be called from the ACI to send the Job_Finished external event
        /// <param name="youtubeURL"></param> The URL of the youtube video to be analyzed
        private static string CreateContainerGroup(IAzure azure,
            string resourceGroupName,
            string containerGroupName,
            string containerImage,
            string callBackURL,
            string youtubeURL)
        {
            Console.WriteLine($"\nCreating container group '{containerGroupName}'...");

            // Get the resource group's region
            IResourceGroup resGroup = azure.ResourceGroups.GetByName(resourceGroupName);
            Region azureRegion = resGroup.Region;

            var identity = azure.Identities.GetById(Environment.GetEnvironmentVariable("MSI_ID"));
            Console.WriteLine(identity);
            Console.WriteLine($"{resourceGroupName}");

            // Create the container group
            var containerGroup = azure.ContainerGroups.Define(containerGroupName)
                .WithRegion(azureRegion)
                .WithExistingResourceGroup(resourceGroupName)
                .WithLinux()
                .WithPrivateImageRegistry(Environment.GetEnvironmentVariable("acrname"), Environment.GetEnvironmentVariable("acrusername"), Environment.GetEnvironmentVariable("acrkey"))
                .DefineVolume("temp-volume")
                    .WithExistingReadWriteAzureFileShare(Environment.GetEnvironmentVariable("filesharename"))
                    .WithStorageAccountName(Environment.GetEnvironmentVariable("storageaccountname"))
                    .WithStorageAccountKey(Environment.GetEnvironmentVariable("storageaccountkey"))
                    .Attach()
                .DefineContainerInstance(containerGroupName)
                    .WithImage(Environment.GetEnvironmentVariable("containerimage"))
                    .WithExternalTcpPort(80)
                    .WithCpuCoreCount(1.0)
                    .WithMemorySizeInGB(3)
                    .WithVolumeMountSetting("temp-volume", "/workdir")
                    .WithEnvironmentVariable("CALLBACK_URL", callBackURL)
                    .WithEnvironmentVariable("STORAGE_CONTAINER", "youtube")
                    .WithEnvironmentVariable("YOUTUBE_URL", youtubeURL)
                    .WithEnvironmentVariable("STORAGE_ACCOUNT", Environment.GetEnvironmentVariable("storageaccountname"))
                    .Attach()
                .WithDnsPrefix(containerGroupName)
                .WithExistingUserAssignedManagedServiceIdentity(identity)
                .Create();

            Console.WriteLine($"Once DNS has propagated, container group '{containerGroup.Name}' will be reachable at http://{containerGroup.Fqdn}");
            return containerGroup.IPAddress;
        }


        [FunctionName("Orchestrator_Delete_ACI_Group")]
        public static string DeleteACIGroup([ActivityTrigger] string name, ILogger log)
        {
            AzureCredentialsFactory factory = new AzureCredentialsFactory();
            AzureCredentials msiCred = factory.FromMSI(new MSILoginInformation(MSIResourceType.AppService, clientId: Environment.GetEnvironmentVariable("clientId")), AzureEnvironment.AzureGlobalCloud);
            var azure = Microsoft.Azure.Management.Fluent.Azure.Configure().Authenticate(msiCred).WithDefaultSubscription();
            DeleteContainerGroup(azure, "ML-Training-RG", name);

            log.LogInformation($"Deleted ACI {name}.");
            return $"Deleted ACI {name}!";
        }

        /// <summary>
        /// Deletes the specified container group.
        /// </summary>
        /// <param name="azure">An authenticated IAzure object.</param>
        /// <param name="resourceGroupName">The name of the resource group containing the container group.</param>
        /// <param name="containerGroupName">The name of the container group to delete.</param>
        private static void DeleteContainerGroup(IAzure azure, string resourceGroupName, string containerGroupName)
        {
            IContainerGroup containerGroup = null;

            while (containerGroup == null)
            {
                containerGroup = azure.ContainerGroups.GetByResourceGroup(resourceGroupName, containerGroupName);

                SdkContext.DelayProvider.Delay(1000);
            }

            Console.WriteLine($"Deleting container group '{containerGroupName}'...");

            azure.ContainerGroups.DeleteById(containerGroup.Id);
        }
    }
}