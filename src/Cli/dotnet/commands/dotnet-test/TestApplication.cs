﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using Microsoft.DotNet.Tools.Test;

namespace Microsoft.DotNet.Cli
{
    internal sealed class TestApplication : IDisposable
    {
        private readonly Module _module;

        private readonly string[] _args;
        private readonly List<string> _outputData = [];
        private readonly List<string> _errorData = [];
        private readonly PipeNameDescription _pipeNameDescription = NamedPipeServer.GetPipeName(Guid.NewGuid().ToString("N"));
        private readonly CancellationTokenSource _cancellationToken = new();

        private NamedPipeServer _pipeConnection;
        private Task _namedPipeConnectionLoop;
        private ConcurrentDictionary<string, string> _executionIds = [];

        public event EventHandler<HandshakeInfoArgs> HandshakeInfoReceived;
        public event EventHandler<HelpEventArgs> HelpRequested;
        public event EventHandler<DiscoveredTestEventArgs> DiscoveredTestReceived;
        public event EventHandler<SuccessfulTestResultEventArgs> SuccessfulTestResultReceived;
        public event EventHandler<FailedTestResultEventArgs> FailedTestResultReceived;
        public event EventHandler<FileArtifactInfoEventArgs> FileArtifactInfoReceived;
        public event EventHandler<SessionEventArgs> SessionEventReceived;
        public event EventHandler<ErrorEventArgs> ErrorReceived;
        public event EventHandler<TestProcessExitEventArgs> TestProcessExited;
        public event EventHandler<EventArgs> Created;
        public event EventHandler<ExecutionEventArgs> ExecutionIdReceived;

        public Module Module => _module;

        public TestApplication(Module module, string[] args)
        {
            _module = module;
            _args = args;
        }

        public void AddExecutionId(string executionId)
        {
            _ = _executionIds.GetOrAdd(executionId, _ => string.Empty);
        }

        public async Task<int> RunAsync(bool isFilterMode, bool enableHelp, BuiltInOptions builtInOptions)
        {
            if (!ModulePathExists())
            {
                return 1;
            }

            bool isDll = _module.DLLPath.EndsWith(".dll");
            ProcessStartInfo processStartInfo = new()
            {
                FileName = isDll ?
                Environment.ProcessPath :
                _module.DLLPath,
                Arguments = enableHelp ? BuildHelpArgs(isDll) : isFilterMode ? BuildArgs(isDll) : BuildArgsWithDotnetRun(builtInOptions),
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _namedPipeConnectionLoop = Task.Run(async () => await WaitConnectionAsync(_cancellationToken.Token), _cancellationToken.Token);
            var result = await StartProcess(processStartInfo);

            _namedPipeConnectionLoop.Wait();
            return result;
        }
        private async Task WaitConnectionAsync(CancellationToken token)
        {
            try
            {
                _pipeConnection = new(_pipeNameDescription, OnRequest, NamedPipeServerStream.MaxAllowedServerInstances, token, skipUnknownMessages: true);
                _pipeConnection.RegisterAllSerializers();

                await _pipeConnection.WaitConnectionAsync(token);
            }
            catch (OperationCanceledException ex) when (ex.CancellationToken == token)
            {
                // We are exiting
            }
            catch (Exception ex)
            {
                if (VSTestTrace.TraceEnabled)
                {
                    VSTestTrace.SafeWriteTrace(() => ex.ToString());
                }

                Environment.FailFast(ex.ToString());
            }
        }

        private Task<IResponse> OnRequest(IRequest request)
        {
            try
            {
                switch (request)
                {
                    case HandshakeInfo handshakeInfo:
                        if (handshakeInfo.Properties.TryGetValue(HandshakeInfoPropertyNames.ModulePath, out string value))
                        {
                            OnHandshakeInfo(handshakeInfo);

                            return Task.FromResult((IResponse)CreateHandshakeInfo(GetSupportedProtocolVersion(handshakeInfo)));
                        }
                        break;

                    case CommandLineOptionMessages commandLineOptionMessages:
                        OnCommandLineOptionMessages(commandLineOptionMessages);
                        break;

                    case DiscoveredTestMessage discoveredTestMessage:
                        OnDiscoveredTestMessage(discoveredTestMessage);
                        break;

                    case SuccessfulTestResultMessage successfulTestResultMessage:
                        OnSuccessfulTestResultMessage(successfulTestResultMessage);
                        break;

                    case FailedTestResultMessage failedTestResultMessage:
                        OnFailedTestResultMessage(failedTestResultMessage);
                        break;

                    case FileArtifactInfo fileArtifactInfo:
                        OnFileArtifactInfo(fileArtifactInfo);
                        break;

                    case TestSessionEvent sessionEvent:
                        OnSessionEvent(sessionEvent);
                        break;

                    // If we don't recognize the message, log and skip it
                    case UnknownMessage unknownMessage:
                        if (VSTestTrace.TraceEnabled)
                        {
                            VSTestTrace.SafeWriteTrace(() => $"Request '{request.GetType()}' with Serializer ID = {unknownMessage.SerializerId} is unsupported.");
                        }
                        return Task.FromResult((IResponse)VoidResponse.CachedInstance);

                    default:
                        // If it doesn't match any of the above, throw an exception
                        throw new NotSupportedException($"Request '{request.GetType()}' is unsupported.");
                }
            }
            catch (Exception ex)
            {
                if (VSTestTrace.TraceEnabled)
                {
                    VSTestTrace.SafeWriteTrace(() => ex.ToString());
                }

                Environment.FailFast(ex.ToString());
            }

            return Task.FromResult((IResponse)VoidResponse.CachedInstance);
        }

        private static string GetSupportedProtocolVersion(HandshakeInfo handshakeInfo)
        {
            handshakeInfo.Properties.TryGetValue(HandshakeInfoPropertyNames.SupportedProtocolVersions, out string protocolVersions);

            string version = string.Empty;
            if (protocolVersions is not null && protocolVersions.Split(";").Contains(ProtocolConstants.Version))
            {
                version = ProtocolConstants.Version;
            }

            return version;
        }

        private static HandshakeInfo CreateHandshakeInfo(string version) =>
            new(new Dictionary<byte, string>
            {
                { HandshakeInfoPropertyNames.PID, Process.GetCurrentProcess().Id.ToString() },
                { HandshakeInfoPropertyNames.Architecture, RuntimeInformation.OSArchitecture.ToString() },
                { HandshakeInfoPropertyNames.Framework, RuntimeInformation.FrameworkDescription },
                { HandshakeInfoPropertyNames.OS, RuntimeInformation.OSDescription },
                { HandshakeInfoPropertyNames.SupportedProtocolVersions, version }
            });

        private async Task<int> StartProcess(ProcessStartInfo processStartInfo)
        {
            if (VSTestTrace.TraceEnabled)
            {
                VSTestTrace.SafeWriteTrace(() => $"Updated args: {processStartInfo.Arguments}");
            }

            var process = Process.Start(processStartInfo);
            StoreOutputAndErrorData(process);
            await process.WaitForExitAsync();

            TestProcessExited?.Invoke(this, new TestProcessExitEventArgs { OutputData = _outputData, ErrorData = _errorData, ExitCode = process.ExitCode });

            return process.ExitCode;
        }

        private void StoreOutputAndErrorData(Process process)
        {
            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;

                _outputData.Add(e.Data);
            };
            process.ErrorDataReceived += (sender, e) =>
            {
                if (string.IsNullOrEmpty(e.Data))
                    return;

                _errorData.Add(e.Data);
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        private bool ModulePathExists()
        {
            if (!File.Exists(_module.DLLPath))
            {
                ErrorReceived.Invoke(this, new ErrorEventArgs { ErrorMessage = $"Test module '{_module.DLLPath}' not found. Build the test application before or run 'dotnet test'." });
                return false;
            }
            return true;
        }

        private string BuildArgsWithDotnetRun(BuiltInOptions builtInOptions)
        {
            StringBuilder builder = new();

            builder.Append($"{CliConstants.DotnetRunCommand} {CliConstants.ProjectOptionKey} \"{_module.ProjectPath}\"");

            if (builtInOptions.HasNoRestore)
            {
                builder.Append($" {TestingPlatformOptions.NoRestoreOption.Name}");
            }

            if (builtInOptions.HasNoBuild)
            {
                builder.Append($" {TestingPlatformOptions.NoBuildOption.Name}");
            }

            if (!string.IsNullOrEmpty(builtInOptions.Architecture))
            {
                builder.Append($" {TestingPlatformOptions.ArchitectureOption.Name} {builtInOptions.Architecture}");
            }

            if (!string.IsNullOrEmpty(builtInOptions.Configuration))
            {
                builder.Append($" {TestingPlatformOptions.ConfigurationOption.Name} {builtInOptions.Configuration}");
            }

            if (!string.IsNullOrEmpty(_module.TargetFramework))
            {
                builder.Append($" {CliConstants.FrameworkOptionKey} {_module.TargetFramework}");
            }

            builder.Append($" {CliConstants.ParametersSeparator} ");

            builder.Append(_args.Length != 0
                ? _args.Aggregate((a, b) => $"{a} {b}")
                : string.Empty);

            builder.Append($" {CliConstants.ServerOptionKey} {CliConstants.ServerOptionValue} {CliConstants.DotNetTestPipeOptionKey} {_pipeNameDescription.Name}");

            return builder.ToString();
        }

        private string BuildArgs(bool isDll)
        {
            StringBuilder builder = new();

            if (isDll)
            {
                builder.Append($"exec {_module.DLLPath} ");
            }

            builder.Append(_args.Length != 0
                ? _args.Aggregate((a, b) => $"{a} {b}")
                : string.Empty);

            builder.Append($" {CliConstants.ServerOptionKey} {CliConstants.ServerOptionValue} {CliConstants.DotNetTestPipeOptionKey} {_pipeNameDescription.Name}");

            return builder.ToString();
        }

        private string BuildHelpArgs(bool isDll)
        {
            StringBuilder builder = new();

            if (isDll)
            {
                builder.Append($"exec {_module.DLLPath} ");
            }

            builder.Append($" {CliConstants.HelpOptionKey} {CliConstants.ServerOptionKey} {CliConstants.ServerOptionValue} {CliConstants.DotNetTestPipeOptionKey} {_pipeNameDescription.Name}");

            return builder.ToString();
        }

        public void OnHandshakeInfo(HandshakeInfo handshakeInfo)
        {
            if (handshakeInfo.Properties.TryGetValue(HandshakeInfoPropertyNames.ExecutionId, out string executionId))
            {
                AddExecutionId(executionId);
                ExecutionIdReceived?.Invoke(this, new ExecutionEventArgs { ModulePath = _module.DLLPath, ExecutionId = executionId });
            }
            HandshakeInfoReceived?.Invoke(this, new HandshakeInfoArgs { handshakeInfo = handshakeInfo });
        }

        public void OnCommandLineOptionMessages(CommandLineOptionMessages commandLineOptionMessages)
        {
            HelpRequested?.Invoke(this, new HelpEventArgs { CommandLineOptionMessages = commandLineOptionMessages });
        }

        internal void OnDiscoveredTestMessage(DiscoveredTestMessage discoveredTestMessage)
        {
            DiscoveredTestReceived?.Invoke(this, new DiscoveredTestEventArgs { DiscoveredTestMessage = discoveredTestMessage });
        }

        internal void OnSuccessfulTestResultMessage(SuccessfulTestResultMessage successfulTestResultMessage)
        {
            SuccessfulTestResultReceived?.Invoke(this, new SuccessfulTestResultEventArgs { SuccessfulTestResultMessage = successfulTestResultMessage });
        }

        internal void OnFailedTestResultMessage(FailedTestResultMessage failedTestResultMessage)
        {
            FailedTestResultReceived?.Invoke(this, new FailedTestResultEventArgs { FailedTestResultMessage = failedTestResultMessage });
        }

        internal void OnFileArtifactInfo(FileArtifactInfo fileArtifactInfo)
        {
            FileArtifactInfoReceived?.Invoke(this, new FileArtifactInfoEventArgs { FileArtifactInfo = fileArtifactInfo });
        }

        internal void OnSessionEvent(TestSessionEvent sessionEvent)
        {
            SessionEventReceived?.Invoke(this, new SessionEventArgs { SessionEvent = sessionEvent });
        }

        internal void OnCreated()
        {
            Created?.Invoke(this, EventArgs.Empty);
        }

        public override string ToString()
        {
            StringBuilder builder = new();

            if (!string.IsNullOrEmpty(_module.DLLPath))
            {
                builder.Append($"DLL: {_module.DLLPath}");
            }

            if (!string.IsNullOrEmpty(_module.ProjectPath))
            {
                builder.Append($"Project: {_module.ProjectPath}");
            };

            if (!string.IsNullOrEmpty(_module.TargetFramework))
            {
                builder.Append($"Target Framework: {_module.TargetFramework}");
            };

            return builder.ToString();
        }

        public void Dispose()
        {
            _pipeConnection?.Dispose();
        }
    }
}
