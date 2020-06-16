using System.IO;
using System.Text;
using System.Threading.Tasks;
using GitHub.Runner.Common;
using GitHub.Runner.Sdk;
using GitHub.DistributedTask.WebApi;
using Pipelines = GitHub.DistributedTask.Pipelines;
using System;
using System.Linq;
using GitHub.DistributedTask.ObjectTemplating.Tokens;
using System.Collections.Generic;
using GitHub.DistributedTask.Pipelines.ContextData;

namespace GitHub.Runner.Worker.Handlers
{
    [ServiceLocator(Default = typeof(CompositeActionHandler))]
    public interface ICompositeActionHandler : IHandler
    {
        CompositeActionExecutionData Data { get; set; }
    }

    // TODO: IMPLEMENT LOGIC FOR HANDLER CODE
    public sealed class CompositeActionHandler : Handler, ICompositeActionHandler
    {
        public CompositeActionExecutionData Data { get; set; }

        // TODO: Create custom PrintActionDetails() to display all steps in unison

        public Task RunAsync(ActionRunStage stage)
        {
            // Basically, the only difference from ScriptHandler.cs is that "contents" is not just each step under "run: "
            // It might make more sense to:
            // 1) Abstract the core functionality of the ScriptHandler.cs that we need for BOTH CompositeActionHandler.cs and ScriptHandler.cs
            // 2) Call those functions in both handlers
            // * There is already a file called ScriptHandlerHelpers.cs that might be a good location to add more functions. 

            // Copied from ScriptHandler.cs
            // Validate args.
            Trace.Entering();
            ArgUtil.NotNull(ExecutionContext, nameof(ExecutionContext));
            ArgUtil.NotNull(Inputs, nameof(Inputs));

            var githubContext = ExecutionContext.ExpressionValues["github"] as GitHubContext;
            ArgUtil.NotNull(githubContext, nameof(githubContext));

            var tempDirectory = HostContext.GetDirectory(WellKnownDirectory.Temp);

            // Resolve action steps
            var actionSteps = Data.Steps;

            // if (Data.End == True)
            if (actionSteps == null)
            {
                Trace.Error("Data.Steps in CompositeActionHandler is null");
            }
            else
            {
                Trace.Info($"Data Steps Value for Composite Actions is: {actionSteps}.");
            }

            // We add each step to JobSteps
            // First clear composite steps
            // ExecutionContext.NewCompositeSteps();

            // TODO: Convert ExecutionContext.CompositeActionSteps to simple local variable
            var compositeActionSteps = new Queue<IStep>();
            foreach (Pipelines.ActionStep aStep in actionSteps)
            {
                // Set current step 
                // This will basically let us recursively to go through each step
                // (when people will call another composite action and so on)
                // This basically represents a stack:
                //          Since this will recursively call RunAsync(), when we recurse back, we'll get the correct "Current Step" to run
                // Ex: 
                // 
                // runs:å
                //      using: "composite"
                //      steps:
                //          - uses: example/test-composite@v2 (a)
                //          - run echo hello world (b)
                //          - run echo hello world 2 (c)
                // 
                // ethanchewy/test-composite/action.yaml
                // runs:
                //      using: "composite"
                //      steps: 
                //          - run echo hello world 3 (d)
                //          - run echo hello world 4 (e)
                // 
                // Stack (LIFO) [Bottom => Middle => Top]:
                // | a |
                // | a | => | d |
                // (Run step d)
                // | a | 
                // | a | => | e |
                // (Run step e)
                // | a | 
                // (Run step a)
                // | b | 
                // (Run step b)
                // | c |
                // (Run step c)
                // Done.

                // 6/15/20 TODO:
                // Replicate behavior for adding post steps in ActionRunner.cs (line 94)
                // Make sure that we explicitly add a script step. 
                // Basically we add to the list of steps that the steprunner has to process
                // We should put it at the top of the list of steps because that's probably the 
                // intention of the author to run the composed action first before the outer ones. 
                // ^^^^
                // 6/16/20
                var actionRunner = HostContext.CreateService<IActionRunner>();
                actionRunner.Action = aStep;
                actionRunner.Stage = stage;
                actionRunner.Condition = aStep.Condition;
                actionRunner.DisplayName = aStep.DisplayName;
                // TODO: Do we need to add any context data from the job message?
                // (See JobExtension.cs ~line 236)

                // Copied from JobExtension since we don't want to add it as a post step
                compositeActionSteps.Enqueue(ExecutionContext.RegisterCompositeStep(actionRunner, Inputs));
            }
            ExecutionContext.EnqueueAllCompositeSteps(compositeActionSteps);

            // Do we need to run anything here below?

            // TODO: Figure out what to return below 
            // We don't have to do anything because we leave it to the job runner
            // Hopefully this doesn't mess up the GUI
            return Task.CompletedTask;
        }

    }
}
