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

        // TODO: Implement PrintActionDetails()
        public override void PrintActionDetails(ActionRunStage stage)
        {
            // Just keep as same as ScriptHandler.cs for now
            var target = Data.Steps;
            var runStepInputs = target[0].Inputs;
            var templateEvaluator = ExecutionContext.ToPipelineTemplateEvaluator();
            var inputs = templateEvaluator.EvaluateStepInputs(runStepInputs, ExecutionContext.ExpressionValues, ExecutionContext.ExpressionFunctions);
            var taskManager = HostContext.GetService<IActionManager>();
            var userInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var runValue = "";
            foreach (KeyValuePair<string, string> input in inputs)
            {
                userInputs.Add(input.Key);
                userInputs.Add(input.Value);
                if (input.Key.Equals("run"))
                {
                    runValue = input.Value;
                }
            }
            var contents = runValue ?? string.Empty;
            if (Action.Type == Pipelines.ActionSourceType.Repository)
            {
                var firstLine = contents.TrimStart(' ', '\t', '\r', '\n');
                var firstNewLine = firstLine.IndexOfAny(new[] { '\r', '\n' });
                if (firstNewLine >= 0)
                {
                    firstLine = firstLine.Substring(0, firstNewLine);
                }

                ExecutionContext.Output($"##[group]Run {firstLine}");
            }
            else
            {
                throw new InvalidOperationException($"Invalid action type {Action.Type} for {nameof(ScriptHandler)}");
            }

            var multiLines = contents.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            foreach (var line in multiLines)
            {
                // Bright Cyan color
                ExecutionContext.Output($"\x1b[36;1m{line}\x1b[0m");
            }

            string argFormat;
            string shellCommand;
            string shellCommandPath = null;
            bool validateShellOnHost = !(StepHost is ContainerStepHost);
            string prependPath = string.Join(Path.PathSeparator.ToString(), ExecutionContext.PrependPath.Reverse<string>());
            string shell = null;
            if (!Inputs.TryGetValue("shell", out shell) || string.IsNullOrEmpty(shell))
            {
                // TODO: figure out how defaults interact with template later
                // for now, we won't check job.defaults if we are inside a template.
                if (string.IsNullOrEmpty(ExecutionContext.ScopeName) && ExecutionContext.JobDefaults.TryGetValue("run", out var runDefaults))
                {
                    runDefaults.TryGetValue("shell", out shell);
                }
            }
            if (string.IsNullOrEmpty(shell))
            {
#if OS_WINDOWS
                shellCommand = "pwsh";
                if (validateShellOnHost)
                {
                    shellCommandPath = WhichUtil.Which(shellCommand, require: false, Trace, prependPath);
                    if (string.IsNullOrEmpty(shellCommandPath))
                    {
                        shellCommand = "powershell";
                        Trace.Info($"Defaulting to {shellCommand}");
                        shellCommandPath = WhichUtil.Which(shellCommand, require: true, Trace, prependPath);
                    }
                }
#else
                shellCommand = "sh";
                if (validateShellOnHost)
                {
                    shellCommandPath = WhichUtil.Which("bash", false, Trace, prependPath) ?? WhichUtil.Which("sh", true, Trace, prependPath);
                }
#endif
                argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
            }
            else
            {
                var parsed = ScriptHandlerHelpers.ParseShellOptionString(shell);
                shellCommand = parsed.shellCommand;
                if (validateShellOnHost)
                {
                    shellCommandPath = WhichUtil.Which(parsed.shellCommand, true, Trace, prependPath);
                }

                argFormat = $"{parsed.shellArgs}".TrimStart();
                if (string.IsNullOrEmpty(argFormat))
                {
                    argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
                }
            }

            if (!string.IsNullOrEmpty(shellCommandPath))
            {
                ExecutionContext.Output($"shell: {shellCommandPath} {argFormat}");
            }
            else
            {
                ExecutionContext.Output($"shell: {shellCommand} {argFormat}");
            }

            if (this.Environment?.Count > 0)
            {
                ExecutionContext.Output("env:");
                foreach (var env in this.Environment)
                {
                    ExecutionContext.Output($"  {env.Key}: {env.Value}");
                }
            }

            ExecutionContext.Output("##[endgroup]");
        }

        public async Task RunAsync(ActionRunStage stage)
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
            if (actionSteps == null) {
                Trace.Error("Data.Steps in CompositeActionHandler is null");
            } else {
                Trace.Info($"Data Steps Value for Composite Actions is: {actionSteps}.");
            }

            // For each action step, we call ActionRunner::RunAsync()
            // How do we get the correct instance of the ActionRunner tho.
            // ^ We can just pass it to our ExecutionContext!!
            foreach (Pipelines.ActionStep aStep in actionSteps) 
            {
                // Set current step 
                // This will basically let us recursively to go through each step
                // (when people will call another composite action and so on)
                // This basically represents a stack:
                //          Since this will recursively call RunAsync(), when we recurse back, we'll get the correct "Current Step" to run
                // Ex: 
                // 
                // runs:
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

                
            }

            // Wait, I don't think we need to do anything after the for loop lol because the processing should handle itself and eventually run the 
            // ScriptHandler

            // WE WANT TO EVENTUALLY GET RID OF ALL THE LOGIC BELOW AND JUST CALL SCRIPTHANDLER FOR THE RUN STUFF
            

            // If the for loop has ended, that means we want to process the current step to eventually return back up recursively
            var runStepInputs = ExecutionContext.CurrentActionStep.Inputs;



            // OUTDATED COMMENTS:
            // How do we stop the recursion? => (if the for loop doesn't run)
            // How do we get the correct runValue?
            // Data.Steps would be different, right?
            // => It should just be Step not a list of steps 
            // ^ Or we could just have another attribute to say the Steps is only 1 step or is empty?!!
            // ^ Maybe the attribute could be called "End" => but couldn't we just check the size of the .Steps?
            // We really need to consider the case when we get the individual Step. 
            // ^ Oh wait, this doesn't make sense ActionManifestManager::ConvertRuns is not run since it will just see "run" instead of "runs"
            // Then what is called when we process each step? if ConvertRuns is not called
            // ^
            var runValue = "";
            if (runStepInputs == null) {
                Trace.Error("runStepInputs in CompositeActionHandler is null");
            } else {
                Trace.Info($"runStepInputs Value for Composite Actions is: {runStepInputs}.");
            }
            var templateEvaluator = ExecutionContext.ToPipelineTemplateEvaluator();
            var inputs = templateEvaluator.EvaluateStepInputs(runStepInputs, ExecutionContext.ExpressionValues, ExecutionContext.ExpressionFunctions);
            var taskManager = HostContext.GetService<IActionManager>();

            // TODO: Support more than running a simple script
            // Right now, we are only supporting a simple run statement
            foreach (KeyValuePair<string, string> input in inputs)
            {
                // userInputs.Add(input.Key);
                // userInputs.Add(input.Value);
                Trace.Info($"Composite Action Handler. Key: {input.Key} Value: {input.Value}");
                if (input.Key.Equals("script"))
                {
                    runValue = input.Value;
                }
            }

            Trace.Info($"Run Value for Composite Actions is: {runValue}.");

            // Let's think about validating inputs later
            // Validate inputs only for actions with action.yml
            // var unexpectedInputs = new List<string>();
            // foreach (var input in userInputs)
            // {
            //     if (!validInputs.Contains(input))
            //     {
            //         unexpectedInputs.Add(input);
            //     }
            // }

            // if (unexpectedInputs.Count > 0)
            // {
            //     ExecutionContext.Warning($"Unexpected input(s) '{string.Join("', '", unexpectedInputs)}', valid inputs are ['{string.Join("', '", validInputs)}']");
            // }

            var contents = runValue ?? string.Empty;

            string workingDirectory = null;
            if (!Inputs.TryGetValue("workingDirectory", out workingDirectory))
            {
                // TODO: figure out how defaults interact with template later
                // for now, we won't check job.defaults if we are inside a template.
                if (string.IsNullOrEmpty(ExecutionContext.ScopeName) && ExecutionContext.JobDefaults.TryGetValue("run", out var runDefaults))
                {
                    if (runDefaults.TryGetValue("working-directory", out workingDirectory))
                    {
                        ExecutionContext.Debug("Overwrite 'working-directory' base on job defaults.");
                    }
                }
            }
            var workspaceDir = githubContext["workspace"] as StringContextData;
            workingDirectory = Path.Combine(workspaceDir, workingDirectory ?? string.Empty);


            string shell = null;
            if (!Inputs.TryGetValue("shell", out shell) || string.IsNullOrEmpty(shell))
            {
                // TODO: figure out how defaults interact with template later
                // for now, we won't check job.defaults if we are inside a template.
                if (string.IsNullOrEmpty(ExecutionContext.ScopeName) && ExecutionContext.JobDefaults.TryGetValue("run", out var runDefaults))
                {
                    if (runDefaults.TryGetValue("shell", out shell))
                    {
                        ExecutionContext.Debug("Overwrite 'shell' base on job defaults.");
                    }
                }
            }

            var isContainerStepHost = StepHost is ContainerStepHost;

            string prependPath = string.Join(Path.PathSeparator.ToString(), ExecutionContext.PrependPath.Reverse<string>());
            string commandPath, argFormat, shellCommand;

            if (string.IsNullOrEmpty(shell))
            {
#if OS_WINDOWS
                shellCommand = "pwsh";
                commandPath = WhichUtil.Which(shellCommand, require: false, Trace, prependPath);
                if (string.IsNullOrEmpty(commandPath))
                {
                    shellCommand = "powershell";
                    Trace.Info($"Defaulting to {shellCommand}");
                    commandPath = WhichUtil.Which(shellCommand, require: true, Trace, prependPath);
                }
                ArgUtil.NotNullOrEmpty(commandPath, "Default Shell");
#else
                shellCommand = "sh";
                commandPath = WhichUtil.Which("bash", false, Trace, prependPath) ?? WhichUtil.Which("sh", true, Trace, prependPath);
#endif
                argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
            }
            else
            {
                var parsed = ScriptHandlerHelpers.ParseShellOptionString(shell);
                shellCommand = parsed.shellCommand;
                // For non-ContainerStepHost, the command must be located on the host by Which
                commandPath = WhichUtil.Which(parsed.shellCommand, !isContainerStepHost, Trace, prependPath);
                argFormat = $"{parsed.shellArgs}".TrimStart();
                if (string.IsNullOrEmpty(argFormat))
                {
                    argFormat = ScriptHandlerHelpers.GetScriptArgumentsFormat(shellCommand);
                }
            }

            // No arg format was given, shell must be a built-in
            if (string.IsNullOrEmpty(argFormat) || !argFormat.Contains("{0}"))
            {
                throw new ArgumentException("Invalid shell option. Shell must be a valid built-in (bash, sh, cmd, powershell, pwsh) or a format string containing '{0}'");
            }

            // We do not not the full path until we know what shell is being used, so that we can determine the file extension
            var scriptFilePath = Path.Combine(tempDirectory, $"{Guid.NewGuid()}{ScriptHandlerHelpers.GetScriptFileExtension(shellCommand)}");
            var resolvedScriptPath = $"{StepHost.ResolvePathForStepHost(scriptFilePath).Replace("\"", "\\\"")}";

            // Format arg string with script path
            var arguments = string.Format(argFormat, resolvedScriptPath);

            // Fix up and write the script
            contents = ScriptHandlerHelpers.FixUpScriptContents(shellCommand, contents);
#if OS_WINDOWS
            // Normalize Windows line endings
            contents = contents.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var encoding = ExecutionContext.Variables.Retain_Default_Encoding && Console.InputEncoding.CodePage != 65001
                ? Console.InputEncoding
                : new UTF8Encoding(false);
#else
            // Don't add a BOM. It causes the script to fail on some operating systems (e.g. on Ubuntu 14).
            var encoding = new UTF8Encoding(false);
#endif
            // Script is written to local path (ie host) but executed relative to the StepHost, which may be a container
            File.WriteAllText(scriptFilePath, contents, encoding);

            // Prepend PATH
            AddPrependPathToEnvironment();

            // expose context to environment
            foreach (var context in ExecutionContext.ExpressionValues)
            {
                if (context.Value is IEnvironmentContextData runtimeContext && runtimeContext != null)
                {
                    foreach (var env in runtimeContext.GetRuntimeEnvironmentVariables())
                    {
                        Environment[env.Key] = env.Value;
                    }
                }
            }

            // dump out the command
            var fileName = isContainerStepHost ? shellCommand : commandPath;
#if OS_OSX
            if (Environment.ContainsKey("DYLD_INSERT_LIBRARIES"))  // We don't check `isContainerStepHost` because we don't support container on macOS
            {
                // launch `node macOSRunInvoker.js shell args` instead of `shell args` to avoid macOS SIP remove `DYLD_INSERT_LIBRARIES` when launch process
                string node12 = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Externals), "node12", "bin", $"node{IOUtil.ExeExtension}");
                string macOSRunInvoker = Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), "macos-run-invoker.js");
                arguments = $"\"{macOSRunInvoker.Replace("\"", "\\\"")}\" \"{fileName.Replace("\"", "\\\"")}\" {arguments}";
                fileName = node12;
            }
#endif
            ExecutionContext.Debug($"{fileName} {arguments}");

            using (var stdoutManager = new OutputManager(ExecutionContext, ActionCommandManager))
            using (var stderrManager = new OutputManager(ExecutionContext, ActionCommandManager))
            {
                StepHost.OutputDataReceived += stdoutManager.OnDataReceived;
                StepHost.ErrorDataReceived += stderrManager.OnDataReceived;

                // Execute
                int exitCode = await StepHost.ExecuteAsync(workingDirectory: StepHost.ResolvePathForStepHost(workingDirectory),
                                            fileName: fileName,
                                            arguments: arguments,
                                            environment: Environment,
                                            requireExitCodeZero: false,
                                            outputEncoding: null,
                                            killProcessOnCancel: false,
                                            inheritConsoleHandler: !ExecutionContext.Variables.Retain_Default_Encoding,
                                            cancellationToken: ExecutionContext.CancellationToken);

                // Error
                if (exitCode != 0)
                {
                    ExecutionContext.Error($"Process completed with exit code {exitCode}.");
                    ExecutionContext.Result = TaskResult.Failed;
                }
            }
        }

    }
}
