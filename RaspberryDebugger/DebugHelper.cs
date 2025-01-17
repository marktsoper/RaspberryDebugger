﻿//-----------------------------------------------------------------------------
// FILE:	    DebugHelper.cs
// CONTRIBUTOR: Jeff Lill
// COPYRIGHT:   Copyright (c) 2021 by neonFORGE, LLC.  All rights reserved.
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

using EnvDTE;
using EnvDTE80;

using Neon.Common;
using Neon.IO;
using Neon.Windows;

using Newtonsoft.Json.Linq;

using Task = System.Threading.Tasks.Task;

namespace RaspberryDebugger
{
    /// <summary>
    /// Remote debugger related utilties.
    /// </summary>
    internal static class DebugHelper
    {
        /// <summary>
        /// Ensures that the native Windows OpenSSH client is installed, prompting
        /// the user to install it if necessary.
        /// </summary>
        /// <returns><c>true</c> if OpenSSH is installed.</returns>
        public static async Task<bool> EnsureOpenSshAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            Log.Info("Checking for native Windows OpenSSH client");

            var openSshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", "OpenSSH", "ssh.exe");

            if (!File.Exists(openSshPath))
            {
                Log.WriteLine("Raspberry debugging requires the native Windows OpenSSH client.  See this:");
                Log.WriteLine("https://techcommunity.microsoft.com/t5/itops-talk-blog/installing-and-configuring-openssh-on-windows-server-2019/ba-p/309540");

                var button = MessageBox.Show(
                    "Raspberry debugging requires the Windows OpenSSH client.\r\n\r\nWould you like to install this now (restart required)?",
                    "Windows OpenSSH Client Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                if (button != DialogResult.Yes)
                {
                    return false;
                }

                // Install via Powershell: https://techcommunity.microsoft.com/t5/itops-talk-blog/installing-and-configuring-openssh-on-windows-server-2019/ba-p/309540

                await PackageHelper.ExecuteWithProgressAsync("Installing OpenSSH Client",
                    async () =>
                    {
                        using (var powershell = new PowerShell())
                        {
                            Log.Info("Installing OpenSSH");
                            Log.Info(powershell.Execute("Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0"));
                        }

                        await Task.CompletedTask;
                    });

                MessageBox.Show(
                    "Restart Windows to complete the OpenSSH Client installation.",
                    "Restart Required",
                    MessageBoxButtons.OK);

                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Attempts to locate the startup project to be debugged, ensuring that it's 
        /// eligable for Raspberry debugging.
        /// </summary>
        /// <param name="dte">The IDE.</param>
        /// <returns>The target project or <c>null</c> if there isn't a startup project or it wasn't eligible.</returns>
        public static Project GetTargetProject(DTE2 dte)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Identify the current startup project (if any).

            if (dte.Solution == null)
            {
                MessageBox.Show(
                    "Please open a Visual Studio solution.",
                    "Solution Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return null;
            }

            var project = PackageHelper.GetStartupProject(dte.Solution);

            if (project == null)
            {
                MessageBox.Show(
                    "Please select a startup project for your solution.",
                    "Startup Project Required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return null;
            }

            // We need to capture the relevant project properties while we're still
            // on the UI thread so we'll have them on background threads.

            var projectProperties = ProjectProperties.CopyFrom(dte.Solution, project);

            if (!projectProperties.IsNetCore)
            {
                MessageBox.Show(
                    "Only .NET Core 3.1 or .NET 5 projects are supported for Raspberry debugging.",
                    "Invalid Project Type",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }

            if (!projectProperties.IsExecutable)
            {
                MessageBox.Show(
                    "Only projects types that generate an executable program are supported for Raspberry debugging.",
                    "Invalid Project Type",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }

            if (string.IsNullOrEmpty(projectProperties.SdkVersion))
            {
                MessageBox.Show(
                    "The .NET Core SDK version could not be identified.",
                    "Invalid Project Type",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }

            var sdkVersion = Version.Parse(projectProperties.SdkVersion);

            if (!projectProperties.IsSupportedSdkVersion)
            {
                MessageBox.Show(
                    $"The .NET Core SDK [{sdkVersion}] is not currently supported.  Only .NET Core versions [v3.1] or later will ever be supported\r\n\r\nNote that we currently support only offical SDKs (not previews or release candidates) and we check for new .NET Core SDKs every week or two.  Submit an issue if you really need support for a new SDK ASAP:\r\n\t\nhttps://github.com/nforgeio/RaspberryDebugger/issues",
                    "SDK Not Supported",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }

            if (projectProperties.AssemblyName.Contains(' '))
            {
                MessageBox.Show(
                    $"Your assembly name [{projectProperties.AssemblyName}] includes a space.  This isn't supported.",
                    "Unsupported Assembly Name",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }

            return project;
        }

        /// <summary>
        /// Builds and publishes a project locally to prepare it for being uploaded to the Raspberry.  This method
        /// will display an error message box to the user on failures
        /// </summary>
        /// <param name="dte"></param>
        /// <param name="solution"></param>
        /// <param name="project"></param>
        /// <param name="projectProperties"></param>
        /// <returns></returns>
        public static async Task<bool> PublishProjectWithUIAsync(DTE2 dte, Solution solution, Project project, ProjectProperties projectProperties)
        {
            Covenant.Requires<ArgumentNullException>(dte != null, nameof(dte));
            Covenant.Requires<ArgumentNullException>(solution != null, nameof(solution));
            Covenant.Requires<ArgumentNullException>(project != null, nameof(project));
            Covenant.Requires<ArgumentNullException>(projectProperties != null, nameof(projectProperties));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!await PublishProjectAsync(dte, solution, project, projectProperties))
            {
                MessageBox.Show(
                    "[dotnet publish] failed for the project.\r\n\r\nLook at the Output/Debug panel for more details.",
                    "Publish Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return false;
            }
            else
            {
                return true;
            }
        }

        /// <summary>
        /// Builds and publishes a project locally to prepare it for being uploaded to the Raspberry.  This method
        /// does not display error message box to the user on failures
        /// </summary>
        /// <param name="dte">The DTE.</param>
        /// <param name="solution">The solution.</param>
        /// <param name="project">The project.</param>
        /// <param name="projectProperties">The project properties.</param>
        /// <returns><c>true</c> on success.</returns>
        public static async Task<bool> PublishProjectAsync(DTE2 dte, Solution solution, Project project, ProjectProperties projectProperties)
        {
            Covenant.Requires<ArgumentNullException>(dte != null, nameof(dte));
            Covenant.Requires<ArgumentNullException>(solution != null, nameof(solution));
            Covenant.Requires<ArgumentNullException>(project != null, nameof(project));
            Covenant.Requires<ArgumentNullException>(projectProperties != null, nameof(projectProperties));

            // Build the project within the context of VS to ensure that all changed
            // files are saved and all dependencies are built first.  Then we'll
            // verify that there were no errors before proceeding.

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            // Ensure that the project is completely loaded by Visual Studio.  I've seen
            // random crashes when building or publishing projects when VS is still loading
            // projects.

            var projectGuid      = projectProperties.Guid;
            var solutionService4 = (IVsSolution4)await RaspberryDebuggerPackage.Instance.GetServiceAsync(typeof(SVsSolution));

            if (solutionService4 == null)
            {
                Covenant.Assert(solutionService4 != null, $"Service [{typeof(SVsSolution).Name}] is not available.");
            }

            solutionService4.EnsureProjectIsLoaded(ref projectGuid, (uint)__VSBSLFLAGS.VSBSLFLAGS_LoadAllPendingProjects);

            // Build the project to ensure that there are no compile-time errors.

            Log.Info($"Building: {projectProperties.FullPath}");

            solution.SolutionBuild.BuildProject(solution.SolutionBuild.ActiveConfiguration.Name, project.UniqueName, WaitForBuildToFinish: true);

            var errorList = dte.ToolWindows.ErrorList.ErrorItems;

            if (errorList.Count > 0)
            {
                for (int i = 1; i <= errorList.Count; i++)
                {
                    var error = errorList.Item(i);

                    Log.Error($"{error.FileName}({error.Line},{error.Column}: {error.Description})");
                }

                Log.Error($"Build failed: [{errorList.Count}] errors");
                Log.Error($"See the Build/Output panel for more information");
                return false;
            }

            Log.Info("Build succeeded");

            // Publish the project so all required binaries and assets end up
            // in the output folder.
            // 
            // Note that we're taking care to only forward a few standard 
            // environment variables because Visual Studio seems to communicate
            // with dotnet related processes with environment variables and 
            // these can cause conflicts when we invoke [dotnet] below to
            // publish the project.

            Log.Info($"Publishing: {projectProperties.FullPath}");

            await Task.Yield();

            var allowedVariableNames =
@"
ALLUSERSPROFILE
APPDATA
architecture
architecture_bits
CommonProgramFiles
CommonProgramFiles(x86)
CommonProgramW6432
COMPUTERNAME
ComSpec
DOTNETPATH
DOTNET_CLI_TELEMETRY_OPTOUT
DriverData
HOME
HOMEDRIVE
HOMEPATH
LOCALAPPDATA
NUMBER_OF_PROCESSORS
OS
Path
PATHEXT
POWERSHELL_DISTRIBUTION_CHANNEL
PROCESSOR_ARCHITECTURE
PROCESSOR_IDENTIFIER
PROCESSOR_LEVEL
PROCESSOR_REVISION
ProgramData
ProgramFiles
ProgramFiles(x86)
ProgramW6432
PUBLIC
SystemDrive
SystemRoot
TEMP
USERDOMAIN
USERDOMAIN_ROAMINGPROFILE
USERNAME
USERPROFILE
windir
";
            var allowedVariables     = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var environmentVariables = new Dictionary<string, string>();

            using (var reader = new StringReader(allowedVariableNames))
            {
                foreach (var line in reader.Lines())
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    allowedVariables.Add(line.Trim());
                }
            }

            foreach (string variable in Environment.GetEnvironmentVariables().Keys)
            {
                if (allowedVariables.Contains(variable))
                {
                    environmentVariables[variable] = Environment.GetEnvironmentVariable(variable);
                }
            }

            try
            {
                var response = await NeonHelper.ExecuteCaptureAsync(
                    "dotnet",
                    new object[]
                    {
                        "publish",
                        "--configuration", projectProperties.Configuration,
                        "--runtime", projectProperties.Runtime,
                        "--no-self-contained",
                        "--output", projectProperties.PublishFolder,
                        projectProperties.FullPath
                    },
                    environmentVariables: environmentVariables);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                if (response.ExitCode == 0)
                {
                    Log.Error("Publish succeeded");
                    return true;
                }

                Log.Error($"Publish failed: ExitCode={response.ExitCode}");
                Log.WriteLine(response.AllText);

                return false;
            }
            catch (Exception e)
            {
                Log.Error(NeonHelper.ExceptionError(e));
                
                return false;
            }
        }

        /// <summary>
        /// Maps the debug connection name we got from the project properties (if any) to
        /// one of our Raspberry connections.  If no name is specified, we'll
        /// use the default connection or prompt the user to create a connection.
        /// We'll display an error if a connection is specified and but doesn't exist.
        /// </summary>
        /// <param name="projectProperties">The project properties.</param>
        /// <returns>The connection or <c>null</c> when one couldn't be located.</returns>
        public static ConnectionInfo GetDebugConnectionInfo(ProjectProperties projectProperties)
        {
            Covenant.Requires<ArgumentNullException>(projectProperties != null, nameof(projectProperties));

            var existingConnections = PackageHelper.ReadConnections();
            var connectionInfo      = (ConnectionInfo)null;

            if (string.IsNullOrEmpty(projectProperties.DebugConnectionName))
            {
                connectionInfo = existingConnections.SingleOrDefault(info => info.IsDefault);

                if (connectionInfo == null)
                {
                    if (MessageBoxEx.Show(
                        $"Raspberry connection information required.  Would you like to create a connection now?",
                        "Raspberry Connection Required",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Error,
                        MessageBoxDefaultButton.Button1) == DialogResult.No)
                    {
                        return null;
                    }

                    connectionInfo = new ConnectionInfo();

                    var connectionDialog = new ConnectionDialog(connectionInfo, edit: false, existingConnections: existingConnections);

                    if (connectionDialog.ShowDialog() == DialogResult.OK)
                    {
                        existingConnections.Add(connectionInfo);
                        PackageHelper.WriteConnections(existingConnections, disableLogging: true);
                    }
                    else
                    {
                        return null;
                    }
                }
            }
            else
            {
                connectionInfo = existingConnections.SingleOrDefault(info => info.Name.Equals(projectProperties.DebugConnectionName, StringComparison.InvariantCultureIgnoreCase));

                if (connectionInfo == null)
                {
                    MessageBoxEx.Show(
                        $"The [{projectProperties.DebugConnectionName}] Raspberry connection does not exist.\r\n\r\nPlease add the connection via:\r\n\r\nTools/Options/Raspberry Debugger",
                        "Cannot Find Raspberry Connection",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);

                    return null;
                }
            }

            return connectionInfo;
        }

        /// <summary>
        /// Determine which .NET SDK we should target for the project.
        /// </summary>
        /// <param name="projectProperties">The project properties.</param>
        /// <returns>The target <see cref="SDK"/> or <c>null</c> when one could not be located.</returns>
        public static Sdk GetTargetSdk(ProjectProperties projectProperties)
        {
            Covenant.Requires<ArgumentNullException>(projectProperties != null, nameof(projectProperties));

            // We're just going to return a matching SDK from the catalog (if it exists).
            // Note that there be maore than one match a standalone SDK or one that ships
            // with Visual Studio.  We'll favor the standalone one if possible.

            var sdkItem = PackageHelper.SdkGoodCatalog.Items.SingleOrDefault(item => item.Version == projectProperties.SdkVersion && 
                                                                                 item.IsStandalone &&
                                                                                 item.Architecture == SdkArchitecture.ARM32);
            if (sdkItem == null)
            {
                // Look for a Visual Studio SDK instead.

                sdkItem = PackageHelper.SdkGoodCatalog.Items.SingleOrDefault(item => item.Version == projectProperties.SdkVersion &&
                                                                                 item.Architecture == SdkArchitecture.ARM32);
            }

            if (sdkItem == null)
            {
                MessageBoxEx.Show(
                    $".NET Core SDK [v{projectProperties.SdkVersion}] is unknown to the Raspberry Debugger.  Please submit an issue at:\r\n\r\n{PackageHelper.GitHubIssuesUri}",
                    "Unknown .NET Core SDK",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                return null;
            }
            else
            {
                return new Sdk(sdkItem.Name, sdkItem.Version);
            }
        }

        /// <summary>
        /// Establishes a connection to the Raspberry and ensures that the Raspberry has
        /// the target SDK, <b>vsdbg</b> installed and also handles uploading of the project
        /// binaries.
        /// </summary>
        /// <param name="connectionInfo">The connection info.</param>
        /// <param name="targetSdk">The target SDK.</param>
        /// <param name="projectProperties">The project properties.</param>
        /// <param name="projectSettings">The project's Raspberry debug settings.</param>
        /// <returns>The <see cref="Connection"/> or <c>null</c> if there was an error.</returns>
        public static async Task<Connection> InitializeConnectionAsync(ConnectionInfo connectionInfo, Sdk targetSdk, ProjectProperties projectProperties, ProjectSettings projectSettings)
        {
            Covenant.Requires<ArgumentNullException>(connectionInfo != null, nameof(connectionInfo));
            Covenant.Requires<ArgumentNullException>(targetSdk != null, nameof(targetSdk));
            Covenant.Requires<ArgumentNullException>(projectProperties != null, nameof(projectProperties));
            Covenant.Requires<ArgumentNullException>(projectSettings != null, nameof(projectSettings));

            var connection = await Connection.ConnectAsync(connectionInfo, projectSettings: projectSettings);

            // .NET Core only supports Raspberry models 3 and 4.

            if (!connection.PiStatus.RaspberryModel.StartsWith("Raspberry Pi 3 Model") &&
                !connection.PiStatus.RaspberryModel.StartsWith("Raspberry Pi 4 Model") &&
                !connection.PiStatus.RaspberryModel.StartsWith("Raspberry Pi Zero 2"))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                MessageBoxEx.Show(
                    $"Your [{connection.PiStatus.RaspberryModel}] is not supported.  .NET Core requires a Raspberry Model 3, 4 or Pi Zero 2.",
                    $"Raspberry Not Supported",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                connection.Dispose();
                return null;
            }

            // Ensure that the SDK is installed.

            if (!await connection.InstallSdkAsync(targetSdk.Version))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                MessageBoxEx.Show(
                    $"Cannot install the .NET SDK [v{targetSdk.Version}] on the Raspberry.\r\n\r\nCheck the Debug Output for more details.",
                    "SDK Installation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                connection.Dispose();
                return null;
            }

            // Ensure that the debugger is installed.

            if (!await connection.InstallDebuggerAsync())
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                MessageBoxEx.Show(
                    $"Cannot install the VSDBG debugger on the Raspberry.\r\n\r\nCheck the Debug Output for more details.",
                    "Debugger Installation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                connection.Dispose();
                return null;
            }

            // Upload the program binaries.

            if (!await connection.UploadProgramAsync(projectProperties.Name, projectProperties.AssemblyName, projectProperties.PublishFolder))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                MessageBoxEx.Show(
                    $"Cannot upload the program binaries to the Raspberry.\r\n\r\nCheck the Debug Output for more details.",
                    "Debugger Installation Failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);

                connection.Dispose();
                return null;
            }

            return connection;
        }
    }
}

