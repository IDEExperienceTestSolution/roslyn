﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Extensions;
using Microsoft.CodeAnalysis.Editor.EditorConfigSettings.Updater;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.ProjectState;

namespace Microsoft.CodeAnalysis.Editor.EditorConfigSettings.DataProvider
{
    internal abstract class SettingsProviderBase<TData, TOptionsUpdater, TOption, TValue> : ISettingsProvider<TData>
        where TOptionsUpdater : ISettingUpdater<TOption, TValue>
    {
        private readonly List<TData> _snapshot = new();
        private static readonly object s_gate = new();
        private ISettingsEditorViewModel? _viewModel;
        protected readonly string FileName;
        protected readonly TOptionsUpdater SettingsUpdater;
        protected readonly Workspace Workspace;

        protected abstract void UpdateOptions(AnalyzerConfigOptions editorConfigOptions, OptionSet visualStudioOptions);

        protected SettingsProviderBase(string fileName, TOptionsUpdater settingsUpdater, Workspace workspace)
        {
            FileName = fileName;
            SettingsUpdater = settingsUpdater;
            Workspace = workspace;
        }

        protected void Update()
        {
            var givenFolder = new DirectoryInfo(FileName).Parent;
            var solution = Workspace.CurrentSolution;
            var projects = solution.GetProjectsForPath(FileName);
            var project = projects.First();
            var configOptionsProvider = new WorkspaceAnalyzerConfigOptionsProvider(project.State);
            var options = configOptionsProvider.GetOptionsForSourcePath(givenFolder.FullName);
            UpdateOptions(options, Workspace.Options);
        }

        public Task<IReadOnlyList<TextChange>?> GetChangedEditorConfigAsync()
            => SettingsUpdater.GetChangedEditorConfigAsync(default);

        public ImmutableArray<TData> GetCurrentDataSnapshot()
        {
            lock (s_gate)
            {
                return _snapshot.ToImmutableArray();
            }
        }

        protected void AddRange(IEnumerable<TData> items)
        {
            lock (s_gate)
            {
                _snapshot.AddRange(items);
            }

            _viewModel?.NotifyOfUpdate();
        }

        public void RegisterViewModel(ISettingsEditorViewModel viewModel)
            => _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }
}
