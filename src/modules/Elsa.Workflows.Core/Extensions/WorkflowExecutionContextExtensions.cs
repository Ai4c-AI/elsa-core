using Elsa.Workflows;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Options;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Elsa.Extensions;

/// <summary>
/// Adds extension methods to <see cref="ActivityExecutionContext"/>.
/// </summary>
public static class WorkflowExecutionContextExtensions
{
    /// <summary>
    /// Schedules the workflow for execution.
    /// </summary>
    public static ActivityWorkItem ScheduleWorkflow(this WorkflowExecutionContext workflowExecutionContext, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
    {
        var workflow = workflowExecutionContext.Workflow;
        var workItem = new ActivityWorkItem(workflow, input: input, variables: variables);
        workflowExecutionContext.Scheduler.Schedule(workItem);
        return workItem;
    }

    /// <summary>
    /// Schedules the root activity of the workflow.
    /// </summary>
    public static ActivityWorkItem ScheduleRoot(this WorkflowExecutionContext workflowExecutionContext, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
    {
        var workflow = workflowExecutionContext.Workflow;
        var workItem = new ActivityWorkItem(workflow.Root, input: input, variables: variables);
        workflowExecutionContext.Scheduler.Schedule(workItem);
        return workItem;
    }

    /// <summary>
    /// Schedules the specified activity of the workflow.
    /// </summary>
    public static ActivityWorkItem ScheduleActivity(this WorkflowExecutionContext workflowExecutionContext, IActivity activity, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
    {
        var workItem = new ActivityWorkItem(activity, input: input, variables: variables);
        workflowExecutionContext.Scheduler.Schedule(workItem);
        return workItem;
    }

    /// <summary>
    /// Schedules the specified activity execution context of the workflow.
    /// </summary>
    public static ActivityWorkItem ScheduleActivityExecutionContext(this WorkflowExecutionContext workflowExecutionContext, ActivityExecutionContext activityExecutionContext, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
    {
        var workItem = new ActivityWorkItem(
            activityExecutionContext.Activity,
            input: input,
            variables: variables,
            existingActivityExecutionContext: activityExecutionContext);
        workflowExecutionContext.Scheduler.Schedule(workItem);
        return workItem;
    }

    /// <summary>
    /// Schedules the activity of the specified bookmark.
    /// </summary>
    /// <returns>The created work item, or <c>null</c> if the specified bookmark doesn't exist in the <see cref="WorkflowExecutionContext"/></returns> 
    public static ActivityWorkItem? ScheduleBookmark(this WorkflowExecutionContext workflowExecutionContext, Bookmark bookmark, IDictionary<string, object>? input = null, IEnumerable<Variable>? variables = null)
    {
        // Get the activity execution context that owns the bookmark.
        var bookmarkedActivityContext = workflowExecutionContext.ActivityExecutionContexts.FirstOrDefault(x => x.Id == bookmark.ActivityInstanceId);
        var logger = workflowExecutionContext.GetRequiredService<ILogger<WorkflowExecutionContext>>();

        if (bookmarkedActivityContext == null)
        {
            logger.LogWarning("Could not find activity execution context with ID {ActivityInstanceId} for bookmark {BookmarkId}", bookmark.ActivityInstanceId, bookmark.Id);
            return null;
        }

        var bookmarkedActivity = bookmarkedActivityContext.Activity;

        // Schedule the activity to resume.
        var workItem = new ActivityWorkItem(bookmarkedActivity)
        {
            ExistingActivityExecutionContext = bookmarkedActivityContext,
            Input = input ?? new Dictionary<string, object>(),
            Variables = variables
        };
        workflowExecutionContext.Scheduler.Schedule(workItem);

        // If no resumption point was specified, use a "noop" to prevent the regular "ExecuteAsync" method to be invoked and instead complete the activity.
        // Unless the bookmark is configured to auto-complete, in which case we'll just complete the activity.
        workflowExecutionContext.ExecuteDelegate = bookmark.CallbackMethodName != null
            ? bookmarkedActivity.GetResumeActivityDelegate(bookmark.CallbackMethodName)
            : bookmark.AutoComplete
                ? WorkflowExecutionContext.Complete
                : WorkflowExecutionContext.Noop;

        // Store the bookmark to resume in the context.
        workflowExecutionContext.ResumedBookmarkContext = new(bookmark);
        logger.LogDebug("Scheduled activity {ActivityId} to resume from bookmark {BookmarkId}", bookmarkedActivity.Id, bookmark.Id);

        return workItem;
    }

    /// <summary>
    /// Schedules the specified activity.
    /// </summary>
    public static ActivityWorkItem Schedule(
        this WorkflowExecutionContext workflowExecutionContext,
        ActivityNode activityNode,
        ActivityExecutionContext owner,
        ScheduleWorkOptions? options = null)
    {
        // Validate that the specified activity is part of the workflow.
        if (!workflowExecutionContext.NodeActivityLookup.ContainsKey(activityNode.Activity))
            throw new InvalidOperationException("The specified activity is not part of the workflow.");

        var scheduler = workflowExecutionContext.Scheduler;

        if (options?.PreventDuplicateScheduling == true)
        {
            // Check if the activity is already scheduled for the specified owner.
            var existingWorkItem = scheduler.Find(x => x.Activity.NodeId == activityNode.NodeId && x.Owner == owner);

            if (existingWorkItem != null)
                return existingWorkItem;
        }

        var activity = activityNode.Activity;
        var tag = options?.Tag;
        var workItem = new ActivityWorkItem(activity, owner, tag, options?.Variables, options?.ExistingActivityExecutionContext, options?.Input);
        var completionCallback = options?.CompletionCallback;

        workflowExecutionContext.AddCompletionCallback(owner, activityNode, completionCallback, tag);
        scheduler.Schedule(workItem);

        return workItem;
    }

    /// <summary>
    /// Returns true if all activities have completed or canceled, false otherwise.
    /// </summary>
    public static bool AllActivitiesCompleted(this WorkflowExecutionContext workflowExecutionContext) => workflowExecutionContext.ActivityExecutionContexts.All(x => x.IsCompleted);

    public static object? GetOutputByActivityId(this WorkflowExecutionContext workflowExecutionContext, string activityId, string? outputName = null)
    {
        var outputRegister = workflowExecutionContext.GetActivityOutputRegister();
        return outputRegister.FindOutputByActivityId(activityId, outputName);
    }

    public static IEnumerable<ActivityExecutionContext> FindActivityExecutionContexts(this WorkflowExecutionContext workflowExecutionContext, ActivityHandle activityHandle)
    {
        if (activityHandle.ActivityInstanceId != null)
            return workflowExecutionContext.ActivityExecutionContexts.Where(x => x.Id == activityHandle.ActivityInstanceId);
        if (activityHandle.ActivityNodeId != null)
            return workflowExecutionContext.ActivityExecutionContexts.Where(x => x.NodeId == activityHandle.ActivityNodeId);
        if (activityHandle.ActivityId != null)
            return workflowExecutionContext.ActivityExecutionContexts.Where(x => x.Activity.Id == activityHandle.ActivityId);
        if (activityHandle.ActivityHash != null)
        {
            var activity = workflowExecutionContext.FindActivityByHash(activityHandle.ActivityHash);
            return activity != null ? workflowExecutionContext.ActivityExecutionContexts.Where(x => x.Activity.NodeId == activity.NodeId) : [];
        }

        return [];
    }
}