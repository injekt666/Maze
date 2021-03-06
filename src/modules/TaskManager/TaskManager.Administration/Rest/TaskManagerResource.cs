using System.Threading.Tasks;
using Maze.Administration.ControllerExtensions;
using Maze.Administration.Library.Clients;
using TaskManager.Shared.Channels;

namespace TaskManager.Administration.Rest
{
    public class TaskManagerResource : ChannelResource<TaskManagerResource>
    {
        public TaskManagerResource() : base("TaskManager")
        {
        }

        public static Task<CallTransmissionChannel<IProcessesProvider>> GetProcessProvider(ITargetedRestClient restClient) =>
            restClient.CreateChannel<TaskManagerResource, IProcessesProvider>("processesProvider");

        public static Task<CallTransmissionChannel<IProcessWatcher>> GetProcessWatcher(ITargetedRestClient restClient) =>
            restClient.CreateChannel<TaskManagerResource, IProcessWatcher>("processWatcher");
    }
}