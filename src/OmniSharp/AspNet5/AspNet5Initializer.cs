﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Framework.DesignTimeHost.Models;
using Microsoft.Framework.DesignTimeHost.Models.IncomingMessages;
using Microsoft.Framework.DesignTimeHost.Models.OutgoingMessages;
using Microsoft.Framework.Logging;
using Microsoft.Framework.OptionsModel;
using Newtonsoft.Json.Linq;
using OmniSharp.Options;
using OmniSharp.Services;

namespace OmniSharp.AspNet5
{
    public class AspNet5Initializer : IWorkspaceInitializer
    {
        private readonly OmnisharpWorkspace _workspace;
        private readonly IOmnisharpEnvironment _env;
        private readonly OmniSharpOptions _options;
        private readonly ILogger _logger;
        private readonly IMetadataFileReferenceCache _metadataFileReferenceCache;
        private readonly DesignTimeHostManager _designTimeHostManager;

        public AspNet5Initializer(OmnisharpWorkspace workspace,
                                  IOmnisharpEnvironment env,
                                  IOptions<OmniSharpOptions> optionsAccessor,
                                  ILoggerFactory loggerFactory,
                                  IMetadataFileReferenceCache metadataFileReferenceCache)
        {
            _workspace = workspace;
            _env = env;
            _options = optionsAccessor.Options;
            _logger = loggerFactory.Create<AspNet5Initializer>();
            _metadataFileReferenceCache = metadataFileReferenceCache;
            _designTimeHostManager = new DesignTimeHostManager(loggerFactory);
        }

        public void Initalize()
        {
            var context = new AspNet5Context();
            context.RuntimePath = GetRuntimePath();
            
            if (!ScanForProjects(context))
            {
                // No ASP.NET 5 projects found so do nothing
                _logger.WriteInformation("No ASP.NET 5 projects found");
                return;
            }

            var wh = new ManualResetEventSlim();

            _designTimeHostManager.Start(context.RuntimePath, context.HostId, port =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Connect(new IPEndPoint(IPAddress.Loopback, port));

                var networkStream = new NetworkStream(socket);

                _logger.WriteInformation("Connected");

                context.Connection = new ProcessingQueue(networkStream, _logger);

                context.Connection.OnReceive += m =>
                {
                    var project = context.Projects[m.ContextId];

                    if (m.MessageType == "ProjectInformation")
                    {
                        var val = m.Payload.ToObject<ProjectMessage>();

                        project.Name = val.Name;
                        project.GlobalJsonPath = val.GlobalJsonPath;
                        project.Configurations = val.Configurations;
                        project.Commands = val.Commands;
                        project.ProjectSearchPaths = val.ProjectSearchPaths;

                        var unprocessed = project.ProjectsByFramework.Keys.ToList();

                        foreach (var frameworkData in val.Frameworks)
                        {
                            unprocessed.Remove(frameworkData.FrameworkName);

                            var frameworkProject = project.ProjectsByFramework.GetOrAdd(frameworkData.FrameworkName, framework =>
                            {
                                return new FrameworkProject(project, framework);
                            });

                            var id = frameworkProject.ProjectId;

                            if (_workspace.CurrentSolution.ContainsProject(id))
                            {
                                continue;
                            }
                            else
                            {
                                var projectInfo = ProjectInfo.Create(
                                        id,
                                        VersionStamp.Create(),
                                        val.Name + "+" + frameworkData.ShortName,
                                        val.Name,
                                        LanguageNames.CSharp);

                                _workspace.AddProject(projectInfo);
                                context.WorkspaceMapping[id] = frameworkProject;
                            }

                            lock (frameworkProject.PendingProjectReferences)
                            {
                                var reference = new Microsoft.CodeAnalysis.ProjectReference(id);

                                foreach (var referenceId in frameworkProject.PendingProjectReferences)
                                {
                                    _workspace.AddProjectReference(referenceId, reference);
                                }

                                frameworkProject.PendingProjectReferences.Clear();
                            }

                        }

                        // Remove old projects
                        foreach (var frameworkName in unprocessed)
                        {
                            FrameworkProject frameworkProject;
                            project.ProjectsByFramework.TryRemove(frameworkName, out frameworkProject);
                            _workspace.RemoveProject(frameworkProject.ProjectId);
                        }
                    }
                    // This is where we can handle messages and update the
                    // language service
                    else if (m.MessageType == "References")
                    {
                        // References as well as the dependency graph information
                        var val = m.Payload.ToObject<ReferencesMessage>();

                        var frameworkProject = project.ProjectsByFramework[val.Framework.FrameworkName];
                        var projectId = frameworkProject.ProjectId;

                        var metadataReferences = new List<MetadataReference>();
                        var projectReferences = new List<Microsoft.CodeAnalysis.ProjectReference>();

                        var removedFileReferences = frameworkProject.FileReferences.ToDictionary(p => p.Key, p => p.Value);
                        var removedRawReferences = frameworkProject.RawReferences.ToDictionary(p => p.Key, p => p.Value);
                        var removedProjectReferences = frameworkProject.ProjectReferences.ToDictionary(p => p.Key, p => p.Value);

                        foreach (var file in val.FileReferences)
                        {
                            if (removedFileReferences.Remove(file))
                            {
                                continue;
                            }

                            var metadataReference = _metadataFileReferenceCache.GetMetadataReference(file);
                            frameworkProject.FileReferences[file] = metadataReference;
                            metadataReferences.Add(metadataReference);
                        }

                        foreach (var rawReference in val.RawReferences)
                        {
                            if (removedRawReferences.Remove(rawReference.Key))
                            {
                                continue;
                            }

                            var metadataReference = MetadataReference.CreateFromImage(rawReference.Value);
                            frameworkProject.RawReferences[rawReference.Key] = metadataReference;
                            metadataReferences.Add(metadataReference);
                        }

                        foreach (var projectReference in val.ProjectReferences)
                        {
                            if (removedProjectReferences.Remove(projectReference.Path))
                            {
                                continue;
                            }

                            var projectReferenceContextId = context.ProjectContextMapping[projectReference.Path];

                            var referencedProject = context.Projects[projectReferenceContextId];

                            var referencedFrameworkProject = referencedProject.ProjectsByFramework.GetOrAdd(projectReference.Framework.FrameworkName,
                                framework =>
                                {
                                    return new FrameworkProject(referencedProject, framework);
                                });

                            var projectReferenceId = referencedFrameworkProject.ProjectId;

                            if (_workspace.CurrentSolution.ContainsProject(projectReferenceId))
                            {
                                projectReferences.Add(new Microsoft.CodeAnalysis.ProjectReference(projectReferenceId));
                            }
                            else
                            {
                                lock (referencedFrameworkProject.PendingProjectReferences)
                                {
                                    referencedFrameworkProject.PendingProjectReferences.Add(projectId);
                                }
                            }

                            referencedFrameworkProject.ProjectDependeees[project.Path] = projectId;

                            frameworkProject.ProjectReferences[projectReference.Path] = projectReferenceId;
                        }

                        foreach (var reference in metadataReferences)
                        {
                            _workspace.AddMetadataReference(projectId, reference);
                        }

                        foreach (var projectReference in projectReferences)
                        {
                            _workspace.AddProjectReference(projectId, projectReference);
                        }

                        foreach (var pair in removedProjectReferences)
                        {
                            _workspace.RemoveProjectReference(projectId, new Microsoft.CodeAnalysis.ProjectReference(pair.Value));
                            frameworkProject.ProjectReferences.Remove(pair.Key);

                            // TODO: Update the dependee's list
                        }

                        foreach (var pair in removedFileReferences)
                        {
                            _workspace.RemoveMetadataReference(projectId, pair.Value);
                            frameworkProject.FileReferences.Remove(pair.Key);
                        }

                        foreach (var pair in removedRawReferences)
                        {
                            _workspace.RemoveMetadataReference(projectId, pair.Value);
                            frameworkProject.RawReferences.Remove(pair.Key);
                        }
                    }
                    else if (m.MessageType == "CompilerOptions")
                    {
                        // Configuration and compiler options
                        var val = m.Payload.ToObject<CompilationOptionsMessage>();

                        var projectId = project.ProjectsByFramework[val.Framework.FrameworkName].ProjectId;

                        var options = val.CompilationOptions.CompilationOptions;

                        var specificDiagnosticOptions = options.SpecificDiagnosticOptions
                        .ToDictionary(p => p.Key, p => (ReportDiagnostic)p.Value);

                        var csharpOptions = new CSharpCompilationOptions(
                                outputKind: (OutputKind)options.OutputKind,
                                optimizationLevel: (OptimizationLevel)options.OptimizationLevel,
                                platform: (Platform)options.Platform,
                                generalDiagnosticOption: (ReportDiagnostic)options.GeneralDiagnosticOption,
                                warningLevel: options.WarningLevel,
                                allowUnsafe: options.AllowUnsafe,
                                concurrentBuild: options.ConcurrentBuild,
                                specificDiagnosticOptions: specificDiagnosticOptions
                            );

                        var parseOptions = new CSharpParseOptions(val.CompilationOptions.LanguageVersion,
                                                                  preprocessorSymbols: val.CompilationOptions.Defines);

                        _workspace.SetCompilationOptions(projectId, csharpOptions);
                        _workspace.SetParseOptions(projectId, parseOptions);
                    }
                    else if (m.MessageType == "Sources")
                    {
                        // The sources to feed to the language service
                        var val = m.Payload.ToObject<SourcesMessage>();

                        var frameworkProject = project.ProjectsByFramework[val.Framework.FrameworkName];
                        var projectId = frameworkProject.ProjectId;

                        var unprocessed = new HashSet<string>(frameworkProject.Documents.Keys);

                        foreach (var file in val.Files)
                        {
                            if (unprocessed.Remove(file))
                            {
                                continue;
                            }

                            using (var stream = File.OpenRead(file))
                            {
                                var sourceText = SourceText.From(stream, encoding: Encoding.UTF8);
                                var id = DocumentId.CreateNewId(projectId);
                                var version = VersionStamp.Create();

                                frameworkProject.Documents[file] = id;

                                var loader = TextLoader.From(TextAndVersion.Create(sourceText, version));
                                _workspace.AddDocument(DocumentInfo.Create(id, file, filePath: file, loader: loader));
                            }
                        }

                        foreach (var file in unprocessed)
                        {
                            var docId = frameworkProject.Documents[file];
                            frameworkProject.Documents.Remove(file);
                            _workspace.RemoveDocument(docId);
                        }

                        frameworkProject.Loaded = true;
                    }

                    if (project.ProjectsByFramework.Values.All(p => p.Loaded))
                    {
                        wh.Set();
                    }
                };

                // Start the message channel
                context.Connection.Start();

                // Initialize the ASP.NET 5 projects
                Initialize(context);
            });

            wh.Wait();

            // TODO: Subscribe to _workspace changes and update DTH
            //Thread.Sleep(5000);

            //_workspace._workspaceChanged += (sender, e) =>
            //{
            //    var arg = e;
            //    var kind = e.Kind;

            //    if (e.Kind == _workspaceChangeKind.DocumentChanged || 
            //        e.Kind == _workspaceChangeKind.DocumentAdded || 
            //        e.Kind == _workspaceChangeKind.DocumentRemoved)
            //    {
            //        var frameworkProject = context._workspaceMapping[e.ProjectId];

            //        TriggerDependeees(context, frameworkProject.ProjectState.Path);
            //    }
            //};
        }

        private static void TriggerDependeees(AspNet5Context context, string path)
        {
            var seen = new HashSet<string>();
            var results = new HashSet<int>();
            var stack = new Stack<string>();

            stack.Push(path);

            while (stack.Count > 0)
            {
                var projectPath = stack.Pop();

                if (!seen.Add(projectPath))
                {
                    continue;
                }

                int contextId;
                if (context.ProjectContextMapping.TryGetValue(projectPath, out contextId))
                {
                    results.Add(contextId);

                    foreach (var frameworkProject in context.Projects[contextId].ProjectsByFramework.Values)
                    {
                        foreach (var dependee in frameworkProject.ProjectDependeees.Keys)
                        {
                            stack.Push(dependee);
                        }
                    }
                }
            }

            foreach (var contextId in results)
            {
                var message = new Message();
                message.HostId = context.HostId;
                message.ContextId = contextId;
                message.MessageType = "FilesChanged";
                context.Connection.Post(message);
            }
        }

        private void Initialize(AspNet5Context context)
        {
            foreach (var project in context.Projects.Values)
            {
                if (project.InitializeSent)
                {
                    continue;
                }

                var projectDirectory = Path.GetDirectoryName(project.Path).TrimEnd(Path.DirectorySeparatorChar);

                // Send an InitializeMessage for each project
                var initializeMessage = new InitializeMessage
                {
                    ProjectFolder = projectDirectory,
                };

                // Initialize this project
                context.Connection.Post(new Message
                {
                    ContextId = project.ContextId,
                    MessageType = "Initialize",
                    Payload = JToken.FromObject(initializeMessage),
                    HostId = context.HostId
                });

                project.InitializeSent = true;
            }
        }

        private bool ScanForProjects(AspNet5Context context)
        {
            _logger.WriteInformation(string.Format("Scanning '{0}' for ASP.NET 5 projects", _env.Path));

            var anyProjects = false;

            foreach (var projectFile in Directory.EnumerateFiles(_env.Path, "project.json", SearchOption.AllDirectories))
            {
                int contextId;
                if (!context.TryAddProject(projectFile, out contextId))
                {
                    continue;
                }

                _logger.WriteInformation(string.Format("Found project '{0}'.", projectFile));

                anyProjects = true;
            }

            return anyProjects;
        }

        private static Task ConnectAsync(Socket socket, IPEndPoint endPoint)
        {
            return Task.Factory.FromAsync((cb, state) => socket.BeginConnect(endPoint, cb, state), ar => socket.EndConnect(ar), null);
        }

        private string GetRuntimePath()
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE");

            var kreHome = Path.Combine(home, ".kre");

            var aliasDirectory = Path.Combine(kreHome, "alias");

            _logger.WriteInformation(string.Format("Using configured alias {0}", _options.AspNet5.Alias));

            var aliasFiles = new[] { "{0}.alias", "{0}.txt" };

            foreach (var shortAliasFile in aliasFiles)
            {
                var aliasFile = Path.Combine(aliasDirectory, string.Format(shortAliasFile, _options.AspNet5.Alias));

                if (File.Exists(aliasFile))
                {
                    var version = File.ReadAllText(aliasFile).Trim();

                    _logger.WriteInformation(string.Format("Using KRE version '{0}'.", version));

                    return Path.Combine(kreHome, "packages", version);
                }
            }

            throw new InvalidOperationException("Unable to locate default alias");
        }
    }
}