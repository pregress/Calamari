﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Calamari.Deployment;
using Calamari.Hooks;
using Calamari.Integration.Certificates;
using Calamari.Integration.EmbeddedResources;
using Calamari.Integration.FileSystem;
using Calamari.Integration.Processes;
using Calamari.Integration.Scripting;

namespace Calamari.Kubernetes
{
    public class KubernetesContextScriptWrapper : IScriptWrapper
    {
        private readonly CalamariVariableDictionary variables;
        private readonly WindowsPhysicalFileSystem fileSystem;
        private readonly AssemblyEmbeddedResources embeddedResources;

        public KubernetesContextScriptWrapper(CalamariVariableDictionary variables)
        {
            this.fileSystem = new WindowsPhysicalFileSystem();
            this.embeddedResources = new AssemblyEmbeddedResources();
            this.variables = variables;
        }

        public bool Enabled => !string.IsNullOrEmpty(variables.Get("Octopus.Action.Kubernetes.ClusterUrl", ""));
        public IScriptWrapper NextWrapper { get; set; }

        public CommandResult ExecuteScript(Script script,
            CalamariVariableDictionary variables,
            ICommandLineRunner commandLineRunner,
            StringDictionary environmentVars)
        {
            var workingDirectory = Path.GetDirectoryName(script.File);

            variables.Set("OctopusKubernetesTargetScript", $"\"{script.File}\"");
            variables.Set("OctopusKubernetesTargetScriptParameters", script.Parameters);
            variables.Set("OctopusKubernetesKubeCtlConfig", Path.Combine(workingDirectory, "kubectl-octo.yml"));

            using (var contextScriptFile = new TemporaryFile(CreateContextScriptFile(workingDirectory)))
            {
                return NextWrapper.ExecuteScript(new Script(contextScriptFile.FilePath), variables, commandLineRunner, environmentVars);
            }
        }

        string CreateContextScriptFile(string workingDirectory)
        {
            var azureContextScriptFile = Path.Combine(workingDirectory, "Octopus.KubectlPowershellContext.ps1");
            var contextScript = embeddedResources.GetEmbeddedResourceText(Assembly.GetExecutingAssembly(), "Calamari.Kubernetes.Scripts.KubectlPowershellContext.ps1");
            fileSystem.OverwriteFile(azureContextScriptFile, contextScript);
            return azureContextScriptFile;
        }
    }
}