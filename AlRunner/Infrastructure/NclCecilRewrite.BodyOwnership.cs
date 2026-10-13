// NclCecilRewrite.BodyOwnership — which pass replaced which method body, measured from the IL (#3519).
//
// A later whole-body replacement clears an earlier one, so a target owned by two passes runs only
// the later copy. Most sites clear the body inline, so no registry can hold ownership; the ledger
// is filled only when RewriteNcl(path, ledger) is asked for it, and production pays nothing.
// A whole-body replacement leaves none of the old Instruction objects behind; a prepend or an
// in-place edit does. Blind spot: two inline clears of one target inside ONE pass read as one owner
// unless both went through ReplaceBodyWithHelper / ReplaceBodyConst, which report themselves.

using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AlRunner.Infrastructure;

public static partial class NclCecilRewrite
{
    /// <summary>Whole-body owners per target, keyed by the Cecil full signature of the method.</summary>
    internal sealed class BodyOwnershipLedger
    {
        private readonly Dictionary<string, List<string>> _owners = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _registryKeys = new(StringComparer.Ordinal);

        /// <summary>Every target some pass replaced wholesale, with the passes that did, in run order.</summary>
        public IReadOnlyDictionary<string, List<string>> Owners => _owners;

        /// <summary>The targets owned by more than one whole-body replacement.</summary>
        public IReadOnlyList<KeyValuePair<string, List<string>>> MultiplyOwned()
            => _owners.Where(kv => kv.Value.Count > 1).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();

        /// <summary>The <see cref="CecilOwned"/> key (name and arity, not full signature) of each target.</summary>
        public IReadOnlyDictionary<string, string> RegistryKeys => _registryKeys;

        /// <summary>
        /// Refuse a ledger that holds a target with two whole-body owners, naming the target and both
        /// owners; refuse one that measured nothing, because an enumeration that found no replacement
        /// at all is a broken measurement, not a clean tree (guards-need-a-third-state.md).
        /// </summary>
        public void AssertSingleOwner()
        {
            if (_owners.Count == 0)
                throw new InvalidOperationException(
                    "[Cecil] body-ownership audit saw no whole-body replacement at all — the audit could "
                    + "not measure anything, which is not the same as every target having one owner (#3519).");

            var multiple = MultiplyOwned();
            if (multiple.Count == 0) return;
            throw new InvalidOperationException(
                "[Cecil] a method body has more than one whole-body owner; the later pass clears the earlier "
                + "one, so a fix to the earlier copy does nothing (#3519). Keep one, or make the second an "
                + "explicit surgical edit:" + Environment.NewLine
                + string.Join(Environment.NewLine, multiple.Select(kv =>
                    $"  {kv.Key} <= {string.Join(", ", kv.Value)}")));
        }

        internal void Add(MethodDefinition target, string owner)
        {
            var name = target.FullName;
            if (!_owners.TryGetValue(name, out var list))
                _owners[name] = list = new List<string>();
            list.Add(owner);
            _registryKeys[name] = Key(target);
        }
    }

    /// <summary>Measures, pass by pass, which method bodies were replaced rather than edited.</summary>
    internal sealed class BodyOwnershipAudit
    {
        private readonly BodyOwnershipLedger _ledger;
        private readonly Dictionary<MethodDefinition, Instruction?> _firstInstruction = new();
        private readonly Dictionary<MethodDefinition, int> _helperReports = new();

        public BodyOwnershipAudit(ModuleDefinition module, BodyOwnershipLedger ledger)
        {
            _ledger = ledger;
            foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            {
                var ins = method.Body.Instructions;
                _firstInstruction[method] = ins.Count > 0 ? ins[0] : null;
            }
        }

        /// <summary>A replacement that went through a shared helper reports itself, so two inside one pass count twice.</summary>
        public void NoteHelperReplacement(MethodDefinition target)
        {
            _helperReports.TryGetValue(target, out var n);
            _helperReports[target] = n + 1;
        }

        /// <summary>Close <paramref name="pass"/>: attribute every body it replaced to it, then re-baseline.</summary>
        public void EndPass(string pass)
        {
            var replaced = new Dictionary<MethodDefinition, int>(_helperReports);
            foreach (var method in _firstInstruction.Keys.ToList())
            {
                var previousFirst = _firstInstruction[method];
                var ins = method.Body.Instructions;
                var currentFirst = ins.Count > 0 ? ins[0] : null;
                if (ReferenceEquals(previousFirst, currentFirst)) continue;

                _firstInstruction[method] = currentFirst;
                // An edit keeps the old instruction objects (a prepend moves the first one down);
                // a replacement builds every instruction afresh.
                bool replacedWholesale = previousFirst != null && !ins.Contains(previousFirst);
                if (replacedWholesale && !replaced.ContainsKey(method))
                    replaced[method] = 1;
            }
            foreach (var (target, count) in replaced)
                for (int i = 0; i < count; i++)
                    _ledger.Add(target, pass);
            _helperReports.Clear();
        }
    }

    [ThreadStatic]
    private static BodyOwnershipAudit? s_bodyOwnershipAudit;
}
