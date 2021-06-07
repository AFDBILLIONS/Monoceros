﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace Monoceros {
    public class ComponentCollectRules : GH_Component, IGH_VariableParameterComponent {
        public ComponentCollectRules( ) : base("Collect Rules",
                                               "CollectRules",
                                               "Collect, convert to Explicit, deduplicate, sort and " +
                                               "remove disallowed Monoceros Rules. Automatically " +
                                               "generates an Out Module and its Rules.",
                                               "Monoceros",
                                               "Rule") {
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddParameter(new ModuleParameter(),
                                  "Modules",
                                  "M",
                                  "All Monoceros Modules",
                                  GH_ParamAccess.list);
            pManager.AddParameter(new RuleParameter(),
                                  "Rules Allowed",
                                  "RA",
                                  "All allowed Monoceros Rules",
                                  GH_ParamAccess.list);
            pManager.AddParameter(new RuleParameter(),
                                  "Explicit Rules Disallowed",
                                  "RD",
                                  "All disallowed Monoceros Rules (optional). " +
                                  "OVERRIDES ALL ALLOWED RULES.",
                                  GH_ParamAccess.list);
            pManager[2].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddParameter(new RuleParameter(),
                                  "Rules",
                                  "R",
                                  "Monoceros Rules",
                                  GH_ParamAccess.list);
        }

        /// <summary>
        /// Wrap input geometry into module cages.
        /// </summary>
        /// <param name="DA">The DA object can be used to retrieve data from
        ///     input parameters and to store data in output parameters.</param>
        protected override void SolveInstance(IGH_DataAccess DA) {
            var modules = new List<Module>();
            var allowed = new List<Rule>();
            var disallowed = new List<Rule>();
            var supportTyped = false;

            if (!DA.GetDataList(0, modules)) {
                return;
            }

            if (!DA.GetDataList(1, allowed)) {
                return;
            }

            if (!DA.GetDataList(2, disallowed)) {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "No Disallowed Rules provided." +
                    " The Allowed Rules were deduplicated, sorted and invalid Rules were removed.");
            }

            if (Params.Input.Count < 4 || !DA.GetData(3, ref supportTyped)) {
                supportTyped = false;
            }

            var invalidModuleCount = modules.RemoveAll(module => module == null || !module.IsValid);

            if (invalidModuleCount > 0) {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                                  invalidModuleCount + " Modules are null or invalid and were removed.");
            }

            var invalidAllowedRuleCount = allowed.RemoveAll(rule => rule == null || !rule.IsValid);

            if (invalidAllowedRuleCount > 0) {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                                  invalidAllowedRuleCount + " allowed Rules are null or invalid and were removed.");
            }

            Module.GenerateEmptySingleModule(Config.OUTER_MODULE_NAME,
                                            Config.INDIFFERENT_TAG,
                                            new Rhino.Geometry.Vector3d(1, 1, 1),
                                            out var moduleOut,
                                            out var rulesOut);

            modules.Add(moduleOut);

            var allowedOriginalClean = allowed
                .Where(rule => rule.IsValidWithModules(modules))
                .Distinct();

            if (disallowed == null || !disallowed.Any()) {
                var earlyReturnRules = allowedOriginalClean.ToList();
                earlyReturnRules.Sort();
                DA.SetDataList(0, earlyReturnRules);
                return;
            }

            if (!supportTyped && disallowed.Any(rule => rule.IsTyped)) {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                                  "Typed Rules are not supported in the Disallowed Rules list. " +
                                  "It is an advanced feature and has to be enabled manually.");
                return;
            }

            allowed.AddRange(
                rulesOut.Select(ruleExplicit => new Rule(ruleExplicit))
                );

            var invalidDisallowedRuleCount = disallowed.RemoveAll(rule => rule == null || !rule.IsValid);

            if (invalidDisallowedRuleCount > 0) {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                                  invalidDisallowedRuleCount + " disallowed Rules are null or invalid and were removed.");
            }

            var allowedExplicit = allowedOriginalClean
                .Where(rule => rule.IsExplicit)
                .Select(rule => rule.Explicit)
                .ToList();
            var allowedTyped = allowedOriginalClean
                .Where(rule => rule.IsTyped)
                .Select(rule => rule.Typed);

            var disallowedOriginalClean = disallowed
                .Where(rule => rule.IsValidWithModules(modules))
                .Distinct();

            var disallowedExplicit = disallowedOriginalClean
                .Where(rule => rule.IsExplicit)
                .Select(rule => rule.Explicit)
                .ToList();
            var disallowedTyped = disallowedOriginalClean
                .Where(rule => rule.IsTyped)
                .Select(rule => rule.Typed);

            var allTypedRules = allowedTyped.Concat(disallowedTyped);

            var allTypedByType = new Dictionary<string, List<RuleTyped>>();
            foreach (var rule in allTypedRules) {
                var type = rule.ConnectorType;
                if (allTypedByType.ContainsKey(type)) {
                    allTypedByType[type].Add(rule);
                } else {
                    allTypedByType.Add(type, new List<RuleTyped>() { rule });
                }
            }

            var disallowedTypedByType = new Dictionary<string, List<RuleTyped>>();
            foreach (var rule in disallowedTyped) {
                var type = rule.ConnectorType;
                if (disallowedTypedByType.ContainsKey(type)) {
                    disallowedTypedByType[type].Add(rule);
                } else {
                    disallowedTypedByType.Add(type, new List<RuleTyped>() { rule });
                }
            }

            // unwrap disallowed typed rules and add them to disallowedExplicit
            foreach (var entry in allTypedByType) {
                var type = entry.Key;
                var rules = entry.Value;
                if (disallowedTypedByType.ContainsKey(type)) {
                    var rulesExplicit = rules.SelectMany(rule => rule.ToRulesExplicit(rules, modules));
                    var disallowedRules = disallowedTypedByType[type];
                    foreach (var rule in rulesExplicit) {
                        if (disallowedRules.Any(disallowedRule =>
                            (disallowedRule.ModuleName == rule.SourceModuleName
                             && disallowedRule.ConnectorIndex == rule.SourceConnectorIndex)
                            || (disallowedRule.ModuleName == rule.TargetModuleName
                                && disallowedRule.ConnectorIndex == rule.TargetConnectorIndex))) {
                            disallowedExplicit.Add(rule);
                        }
                    }
                }
            }

            // unwrap all typed rules
            foreach (var rule in allTypedRules) {
                var rulesExplicit = rule.ToRulesExplicit(allTypedRules, modules);
                allowedExplicit.AddRange(rulesExplicit);
            }

            var finalExplicit = allowedExplicit.Except(disallowedExplicit);

            var outputRules = finalExplicit
                .Where(rule => !(rule.SourceModuleName == Config.OUTER_MODULE_NAME && rule.TargetModuleName == Config.OUTER_MODULE_NAME))
                .Distinct()
                .Select(explicitRule => new Rule(explicitRule))
                .ToList();

            outputRules.Sort();

            foreach (var rule in outputRules) {
                if (!rule.IsValid) {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, rule.IsValidWhyNot);
                }
            }

            DA.SetDataList(0, outputRules);
        }

        public bool CanInsertParameter(GH_ParameterSide side, int index) {
            if (side == GH_ParameterSide.Input && index == 3) {
                return true;
            } else {
                return false;
            }
        }

        public bool CanRemoveParameter(GH_ParameterSide side, int index) {
            if (side == GH_ParameterSide.Input && index == 3) {
                return true;
            } else {
                return false;
            }
        }

        public IGH_Param CreateParameter(GH_ParameterSide side, int index) {
            Params.Input[2].Name = "Rules Disallowed";
            var lockSwitch = new Param_Boolean {
                NickName = "U",
                Name = "Unlock Typed",
                Description = "ADVANCED: Unlock support for Disallow Typed Rules",
                Access = GH_ParamAccess.item
            };
            lockSwitch.PersistentData.Append(new GH_Boolean(false));
            return lockSwitch;
        }

        public bool DestroyParameter(GH_ParameterSide side, int index) {
            Params.Input[2].Name = "Explicit Rules Disallowed";
            Params.UnregisterInputParameter(Params.Input[index]);
            ExpireSolution(true);
            return true;
        }

        public void VariableParameterMaintenance( ) {
        }

        /// <summary>
        /// The Exposure property controls where in the panel a component icon
        /// will appear. There are seven possible locations (primary to
        /// septenary), each of which can be combined with the
        /// GH_Exposure.obscure flag, which ensures the component will only be
        /// visible on panel dropdowns.
        /// </summary>
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        /// <summary>
        /// Provides an Icon for every component that will be visible in the
        /// User Interface. Icons need to be 24x24 pixels.
        /// </summary>
        protected override Bitmap Icon => Properties.Resources.rules_collect;

        /// <summary>
        /// Each component must have a unique Guid to identify it.  It is vital
        /// this Guid doesn't change otherwise old ghx files that use the old ID
        /// will partially fail during loading.
        /// </summary>
        public override Guid ComponentGuid => new Guid("8DAA6103-4CB8-466A-B484-23E0D4614ADE");
    }
}
