using System;
﻿using System.Linq;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using CKAN.Versioning;

namespace CKAN
{
    public enum RelationshipType
    {
        Depends    = 0,
        Recommends = 1,
        Suggests   = 2,
        Supports   = 3,
        Conflicts  = 4
    }

    public partial class MainModInfo : UserControl
    {
        private BackgroundWorker          cacheWorker;
        private GUIMod                    selectedModule;
        private NetAsyncModulesDownloader downloader;
        private CkanModule                currentModContentsModule;

        public MainModInfo()
        {
            InitializeComponent();

            cacheWorker = new BackgroundWorker()
            {
                WorkerReportsProgress      = true,
                WorkerSupportsCancellation = true,
            };
            cacheWorker.RunWorkerCompleted += PostModCaching;
            cacheWorker.DoWork += CacheMod;

            DependsGraphTree.BeforeExpand += BeforeExpand;
        }

        public GUIMod SelectedModule
        {
            set
            {
                this.selectedModule = value;
                if (value == null)
                {
                    ModInfoTabControl.Enabled = false;
                }
                else
                {
                    var module = value.ToModule();
                    ModInfoTabControl.Enabled = module != null;
                    if (module == null) return;

                    UpdateModInfo(value);
                    UpdateModDependencyGraph(module);
                    UpdateModContentsTree(module);
                    AllModVersions.SelectedModule = value;
                }
            }
            get
            {
                return selectedModule;
            }
        }

        public int ModMetaSplitPosition
        {
            get
            {
                return splitContainer2.SplitterDistance;
            }
            set
            {
                try
                {
                    this.splitContainer2.SplitterDistance = value;
                }
                catch
                {
                    // SplitContainer is mis-designed to throw exceptions
                    // if the min/max limits are exceeded rather than simply obeying them.
                }
            }
        }

        private KSPManager manager
        {
            get
            {
                return Main.Instance.manager;
            }
        }

        private void DependsGraphTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            Main.Instance.ResetFilterAndSelectModOnList(e.Node.Name);
        }

        private void ContentsPreviewTree_NodeMouseDoubleClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            OpenFileBrowser(e.Node);
        }

        private void ContentsDownloadButton_Click(object sender, EventArgs e)
        {
            StartDownload(SelectedModule);
        }

        private void ContentsOpenButton_Click(object sender, EventArgs e)
        {
            Process.Start(manager.Cache.GetCachedFilename(SelectedModule.ToModule()));
        }

        private void LinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Util.HandleLinkClicked((sender as LinkLabel).Text, e);
        }

        private void UpdateModInfo(GUIMod gui_module)
        {
            CkanModule module = gui_module.ToModule();

            Util.Invoke(MetadataModuleNameTextBox, () => MetadataModuleNameTextBox.Text = gui_module.Name);
            UpdateTagsAndLabels(gui_module.ToModule());
            Util.Invoke(MetadataModuleAbstractLabel, () => MetadataModuleAbstractLabel.Text = gui_module.Abstract);
            Util.Invoke(MetadataModuleDescriptionTextBox, () =>
            {
                MetadataModuleDescriptionTextBox.Text = gui_module.Description
                    ?.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                MetadataModuleDescriptionTextBox.ScrollBars =
                    string.IsNullOrWhiteSpace(gui_module.Description)
                        ? ScrollBars.None
                        : ScrollBars.Vertical;
            });

            Util.Invoke(MetadataModuleVersionTextBox, () => MetadataModuleVersionTextBox.Text = gui_module.LatestVersion.ToString());
            Util.Invoke(MetadataModuleLicenseTextBox, () => MetadataModuleLicenseTextBox.Text = string.Join(", ", module.license));
            Util.Invoke(MetadataModuleAuthorTextBox, () => MetadataModuleAuthorTextBox.Text = gui_module.Authors);
            Util.Invoke(MetadataIdentifierTextBox, () => MetadataIdentifierTextBox.Text = gui_module.Identifier);

            // If we have a homepage provided, use that; otherwise use the spacedock page, curse page or the github repo so that users have somewhere to get more info than just the abstract.
            Util.Invoke(MetadataModuleHomePageLinkLabel, () => MetadataModuleHomePageLinkLabel.Text = gui_module.Homepage.ToString());
            Util.Invoke(MetadataModuleGitHubLinkLabel, () => MetadataModuleGitHubLinkLabel.Text = module.resources?.repository?.ToString() ?? Properties.Resources.MainModInfoNSlashA);
            Util.Invoke(MetadataModuleReleaseStatusTextBox, () => MetadataModuleReleaseStatusTextBox.Text = module.release_status?.ToString() ?? Properties.Resources.MainModInfoNSlashA);
            Util.Invoke(MetadataModuleKSPCompatibilityTextBox, () => MetadataModuleKSPCompatibilityTextBox.Text = gui_module.KSPCompatibilityLong);
            Util.Invoke(ReplacementTextBox, () => ReplacementTextBox.Text = gui_module.ToModule()?.replaced_by?.ToString() ?? Properties.Resources.MainModInfoNSlashA);
        }

        private ModuleLabelList ModuleLabels
        {
            get
            {
                return Main.Instance.mainModList.ModuleLabels;
            }
        }

        private ModuleTagList ModuleTags
        {
            get
            {
                return Main.Instance.mainModList.ModuleTags;
            }
        }

        private void UpdateTagsAndLabels(CkanModule mod)
        {
            Util.Invoke(MetadataTagsLabelsPanel, () =>
            {
                MetadataTagsLabelsPanel.Controls.Clear();
                var tags = ModuleTags?.Tags
                    .Where(t => t.Value.ModuleIdentifiers.Contains(mod.identifier))
                    .Select(t => t.Value);
                if (tags != null)
                {
                    foreach (ModuleTag tag in tags)
                    {
                        MetadataTagsLabelsPanel.Controls.Add(TagLabelLink(
                            tag.Name, tag, new LinkLabelLinkClickedEventHandler(this.TagLinkLabel_LinkClicked)
                        ));
                    }
                }
                var labels = ModuleLabels?.LabelsFor(manager.CurrentInstance.Name)
                    .Where(l => l.ModuleIdentifiers.Contains(mod.identifier));
                if (labels != null)
                {
                    foreach (ModuleLabel mlbl in labels)
                    {
                        MetadataTagsLabelsPanel.Controls.Add(TagLabelLink(
                            mlbl.Name, mlbl, new LinkLabelLinkClickedEventHandler(this.LabelLinkLabel_LinkClicked)
                        ));
                    }
                }
            });
        }

        private LinkLabel TagLabelLink(string name, object tag, LinkLabelLinkClickedEventHandler onClick)
        {
            var link = new LinkLabel()
            {
                AutoSize     = true,
                LinkColor    = SystemColors.GrayText,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Margin       = new Padding(2),
                Text         = name,
                Tag          = tag,
            };
            link.LinkClicked += onClick;
            return link;
        }

        public delegate void ChangeFilter(GUIModFilter filter, ModuleTag tag, ModuleLabel label);
        public event ChangeFilter OnChangeFilter;

        private void TagLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var link = sender as LinkLabel;
            if (OnChangeFilter != null)
            {
                OnChangeFilter(GUIModFilter.Tag, link.Tag as ModuleTag, null);
            }
        }

        private void LabelLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            var link = sender as LinkLabel;
            if (OnChangeFilter != null)
            {
                OnChangeFilter(GUIModFilter.CustomLabel, null, link.Tag as ModuleLabel);
            }
        }

        private void BeforeExpand(object sender, TreeViewCancelEventArgs args)
        {
            // Hourglass cursor
            Cursor prevCur = Cursor.Current;
            Cursor.Current = Cursors.WaitCursor;

            DependsGraphTree.BeginUpdate();

            TreeNode node = args.Node;
            IRegistryQuerier registry = RegistryManager.Instance(manager.CurrentInstance).registry;
            // Should already have children, since the user is expanding it
            foreach (TreeNode child in node.Nodes)
            {
                // If there are grandchildren, then this child has been loaded before
                if (child.Nodes.Count == 0)
                {
                    AddChildren(registry, child);
                }
            }

            DependsGraphTree.EndUpdate();

            Cursor.Current = prevCur;
        }

        private bool ImMyOwnGrandpa(TreeNode node)
        {
            CkanModule module = node.Tag as CkanModule;
            if (module != null)
            {
                for (TreeNode other = node.Parent; other != null; other = other.Parent)
                {
                    if (module == other.Tag)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private void UpdateModDependencyGraph(CkanModule module)
        {
            ModInfoTabControl.Tag = module ?? ModInfoTabControl.Tag;
            // Can be costly. For now only update when visible.
            if (ModInfoTabControl.SelectedIndex != RelationshipTabPage.TabIndex)
            {
                return;
            }
            Util.Invoke(DependsGraphTree, _UpdateModDependencyGraph);
        }

        private void _UpdateModDependencyGraph()
        {
            CkanModule module = (CkanModule)ModInfoTabControl.Tag;

            DependsGraphTree.BeginUpdate();
            DependsGraphTree.Nodes.Clear();
            IRegistryQuerier registry = RegistryManager.Instance(manager.CurrentInstance).registry;
            TreeNode root = new TreeNode($"{module.name} {module.version}", 0, 0)
            {
                Name = module.identifier,
                Tag  = module
            };
            DependsGraphTree.Nodes.Add(root);
            AddChildren(registry, root);
            root.Expand();
            DependsGraphTree.EndUpdate();
        }

        private static readonly RelationshipType[] kindsOfRelationships = new RelationshipType[]
        {
            RelationshipType.Depends,
            RelationshipType.Recommends,
            RelationshipType.Suggests,
            RelationshipType.Supports,
            RelationshipType.Conflicts
        };

        private void AddChildren(IRegistryQuerier registry, TreeNode node)
        {
            // Skip children of nodes from circular dependencies
            if (ImMyOwnGrandpa(node))
                return;

            // Load one layer of grandchildren on demand
            CkanModule module = node.Tag as CkanModule;
            // Tag is null for non-indexed nodes
            if (module != null)
            {
                foreach (RelationshipType relationship in kindsOfRelationships)
                {
                    IEnumerable<RelationshipDescriptor> relationships = null;
                    switch (relationship)
                    {
                        case RelationshipType.Depends:
                            relationships = module.depends;
                            break;
                        case RelationshipType.Recommends:
                            relationships = module.recommends;
                            break;
                        case RelationshipType.Suggests:
                            relationships = module.suggests;
                            break;
                        case RelationshipType.Supports:
                            relationships = module.supports;
                            break;
                        case RelationshipType.Conflicts:
                            relationships = module.conflicts;
                            break;
                    }
                    if (relationships != null)
                    {
                        foreach (RelationshipDescriptor dependency in relationships)
                        {
                            // Look for compatible mods
                            TreeNode child = findDependencyShallow(
                                    registry, dependency, relationship,
                                    manager.CurrentInstance.VersionCriteria())
                                // Then incompatible mods
                                ?? findDependencyShallow(
                                    registry, dependency, relationship, null)
                                // Then give up and note the name without a module
                                ?? nonindexedNode(dependency, relationship);
                            node.Nodes.Add(child);
                        }
                    }
                }
            }
        }

        private TreeNode findDependencyShallow(IRegistryQuerier registry, RelationshipDescriptor relDescr, RelationshipType relationship, KspVersionCriteria crit)
        {
            // Maybe it's a DLC?
            if (relDescr.MatchesAny(
                registry.InstalledModules.Select(im => im.Module),
                new HashSet<string>(registry.InstalledDlls),
                registry.InstalledDlc))
            {
                return nonModuleNode(relDescr, null, relationship);
            }

            // Find modules that satisfy this dependency
            List<CkanModule> dependencyModules = relDescr.LatestAvailableWithProvides(registry, crit);
            if (dependencyModules.Count == 0)
            {
                // Nothing found, don't return a node
                return null;
            }
            else if (dependencyModules.Count == 1
                && relDescr.ContainsAny(new string[] { dependencyModules[0].identifier }))
            {
                // Only one exact match module, return a simple node
                return indexedNode(registry, dependencyModules[0], relationship, crit != null);
            }
            else
            {
                // Several found or not same id, return a "provides" node
                return providesNode(relDescr.ToString(), relationship,
                    dependencyModules.Select(dep => indexedNode(registry, dep, relationship, crit != null))
                );
            }
        }

        private TreeNode providesNode(string identifier, RelationshipType relationship, IEnumerable<TreeNode> children)
        {
            int icon = (int)relationship + 1;
            return new TreeNode(string.Format(Properties.Resources.MainModInfoVirtual, identifier), icon, icon, children.ToArray())
            {
                Name        = identifier,
                ToolTipText = relationship.ToString(),
                ForeColor   = Color.Gray
            };
        }

        private TreeNode indexedNode(IRegistryQuerier registry, CkanModule module, RelationshipType relationship, bool compatible)
        {
            int icon = (int)relationship + 1;
            string suffix = compatible ? ""
                : $" ({registry.CompatibleGameVersions(module.identifier)})";
            return new TreeNode($"{module.name} {module.version}{suffix}", icon, icon)
            {
                Name        = module.identifier,
                ToolTipText = relationship.ToString(),
                Tag         = module,
                ForeColor   = compatible ? Color.Empty : Color.Red
            };
        }

        private TreeNode nonModuleNode(RelationshipDescriptor relDescr, ModuleVersion version, RelationshipType relationship)
        {
            int icon = (int)relationship + 1;
            return new TreeNode($"{relDescr} {version}", icon, icon)
            {
                Name        = relDescr.ToString(),
                ToolTipText = relationship.ToString()
            };
        }

        private TreeNode nonindexedNode(RelationshipDescriptor relDescr, RelationshipType relationship)
        {
            // Completely nonexistent dependency, e.g. "AJE"
            int icon = (int)relationship + 1;
            return new TreeNode(string.Format(Properties.Resources.MainModInfoNotIndexed, relDescr.ToString()), icon, icon)
            {
                Name        = relDescr.ToString(),
                ToolTipText = relationship.ToString(),
                ForeColor   = Color.Red
            };
        }

        // When switching tabs ensure that the resulting tab is updated.
        private void ModInfoIndexChanged(object sender, EventArgs e)
        {
            switch (ModInfoTabControl.SelectedTab.Name)
            {

                case "ContentTabPage":
                    UpdateModContentsTree(null);
                    break;

                case "RelationshipTabPage":
                    UpdateModDependencyGraph(null);
                    break;

                case "AllModVersionsTabPage":
                    if (Platform.IsMono)
                    {
                        // Workaround: make sure the ListView headers are drawn
                        AllModVersions.ForceRedraw();
                    }
                    break;

            }
        }

        public void UpdateModContentsTree(CkanModule module, bool force = false)
        {
            ModInfoTabControl.Tag = module ?? ModInfoTabControl.Tag;
            //Can be costly. For now only update when visible.
            if (ModInfoTabControl.SelectedIndex != ContentTabPage.TabIndex && !force)
            {
                return;
            }
            Util.Invoke(ContentsPreviewTree, () => _UpdateModContentsTree(force));
        }

        private void _UpdateModContentsTree(bool force = false)
        {
            GUIMod guiMod = SelectedModule;
            if (!guiMod.IsCKAN)
            {
                return;
            }
            CkanModule module = guiMod.ToCkanModule();
            if (Equals(module, currentModContentsModule) && !force)
            {
                return;
            }
            else
            {
                currentModContentsModule = module;
            }
            if (!guiMod.IsCached)
            {
                NotCachedLabel.Text = Properties.Resources.MainModInfoNotCached;
                ContentsDownloadButton.Enabled = true;
                ContentsOpenButton.Enabled = false;
                ContentsPreviewTree.Enabled = false;
            }
            else
            {
                NotCachedLabel.Text = Properties.Resources.MainModInfoCached;
                ContentsDownloadButton.Enabled = false;
                ContentsOpenButton.Enabled = true;
                ContentsPreviewTree.Enabled = true;
            }

            ContentsPreviewTree.Nodes.Clear();
            ContentsPreviewTree.Nodes.Add(module.name);

            IEnumerable<string> contents = ModuleInstaller.GetInstance(manager.CurrentInstance, Main.Instance.Manager.Cache, GUI.user).GetModuleContentsList(module);
            if (contents == null)
            {
                return;
            }

            foreach (string item in contents)
            {
                ContentsPreviewTree.Nodes[0].Nodes.Add(item.Replace('/', Path.DirectorySeparatorChar));
            }

            ContentsPreviewTree.Nodes[0].ExpandAll();
        }

        public void StartDownload(GUIMod module)
        {
            if (module == null || !module.IsCKAN)
                return;

            Main.Instance.ShowWaitDialog(false);
            if (cacheWorker.IsBusy)
            {
                Task.Factory.StartNew(() =>
                {
                    // Just pass to the existing worker
                    downloader.DownloadModules(new List<CkanModule> { module.ToCkanModule() });
                    module.UpdateIsCached();
                });
            }
            else
            {
                // Start up a new worker
                downloader = new NetAsyncModulesDownloader(Main.Instance.currentUser, Main.Instance.Manager.Cache);
                cacheWorker.RunWorkerAsync(module);
            }
        }

        // cacheWorker.DoWork
        private void CacheMod(object sender, DoWorkEventArgs e)
        {
            Main.Instance.ResetProgress();
            Main.Instance.ClearLog();

            GUIMod gm = e.Argument as GUIMod;
            downloader.DownloadModules(new List<CkanModule> { gm.ToCkanModule() });
            e.Result = e.Argument;
        }

        // cacheWorker.RunWorkerCompleted
        public void PostModCaching(object sender, RunWorkerCompletedEventArgs e)
        {
            Util.Invoke(this, () => _PostModCaching((GUIMod)e.Result));
        }

        private void _PostModCaching(GUIMod module)
        {
            module.UpdateIsCached();
            Main.Instance.HideWaitDialog(true);
            // User might have selected another row. Show current in tree.
            UpdateModContentsTree(SelectedModule.ToCkanModule(), true);
        }

        /// <summary>
        /// Opens the file browser of the users system
        /// with the folder of the clicked node opened
        /// TODO: Open a file browser with the file selected
        /// </summary>
        /// <param name="node">A node of the ContentsPreviewTree</param>
        internal void OpenFileBrowser(TreeNode node)
        {
            string location = node.Text;

            if (File.Exists(location))
            {
                //We need the Folder of the file
                //Otherwise the OS would try to open the file in its default application
                location = Path.GetDirectoryName(location);
            }

            if (!Directory.Exists(location))
            {
                //User either selected the parent node
                //or he clicked on the tree node of a cached, but not installed mod
                return;
            }

            Process.Start(location);
        }
    }
}
