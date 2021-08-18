using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;


namespace ExtractorOrchestrator
{
    public static class ExtractionFinished
    {
        [FunctionName("ExtractionFinishedTrigger")]
        public static async Task Run(
            [QueueTrigger("extractionfinished")] string instanceId,
            [DurableClient] IDurableOrchestrationClient client)
        {
            await client.RaiseEventAsync(instanceId, "Extractor_Finished", true);
        }
    }
}