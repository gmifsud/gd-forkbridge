namespace ForkBridge.Core.Config;

public static class ConfigValidator
{
    public static readonly string[] Modes = ["pick", "reconcile", "merge"];
    public static readonly string[] ComponentStatuses = ["proposed", "confirmed", "placeholder"];

    public static List<string> Validate(ForkBridgeConfig cfg)
    {
        var e = new List<string>();
        if (cfg.Version != 1) e.Add($"Unsupported config version {cfg.Version} (expected 1).");
        if (cfg.Repositories.Count == 0) e.Add("No repositories defined.");
        foreach (var (alias, r) in cfg.Repositories)
        {
            if (string.IsNullOrWhiteSpace(r.Path)) e.Add($"repositories.{alias}.path is empty.");
            if (r.Path.Contains("<PLACEHOLDER", StringComparison.OrdinalIgnoreCase) && !r.Placeholder)
                e.Add($"repositories.{alias}.path looks like a placeholder; set placeholder: true or provide the real path.");
        }
        foreach (var (name, p) in cfg.Pairs)
        {
            string at = $"pairs.{name}";
            if (p.Products.Count != 2) e.Add($"{at}.products must map exactly two product keys to repository aliases.");
            foreach (var (prod, alias) in p.Products)
                if (!cfg.Repositories.ContainsKey(alias)) e.Add($"{at}.products.{prod} references unknown repository '{alias}'.");
            foreach (var d in p.Directions)
            {
                if (!p.Products.ContainsKey(d.From) || !p.Products.ContainsKey(d.To) || d.From == d.To)
                    e.Add($"{at}.directions: invalid direction {d.From}->{d.To}.");
                foreach (var m in d.Modes) if (!Modes.Contains(m)) e.Add($"{at}.directions {d.From}->{d.To}: unknown mode '{m}'.");
            }
            var ids = new HashSet<string>();
            foreach (var c in p.Components)
            {
                if (string.IsNullOrWhiteSpace(c.Id)) { e.Add($"{at}.components: component without id."); continue; }
                if (!ids.Add(c.Id)) e.Add($"{at}.components: duplicate id '{c.Id}'.");
                if (!ComponentStatuses.Contains(c.Status)) e.Add($"{at}.components.{c.Id}: status must be one of {string.Join(", ", ComponentStatuses)}.");
                foreach (var prod in p.Products.Keys)
                {
                    c.Paths.TryGetValue(prod, out var path);
                    bool absent = c.Absent.ContainsKey(prod);
                    if (string.IsNullOrWhiteSpace(path) && !absent)
                        e.Add($"{at}.components.{c.Id}: no path for '{prod}'. Give a path, or declare it under 'absent' with a reason.");
                    if (!string.IsNullOrWhiteSpace(path) && absent)
                        e.Add($"{at}.components.{c.Id}: '{prod}' has both a path and an absent reason.");
                    if (path is not null && (path.Contains("..") || Path.IsPathRooted(path)))
                        e.Add($"{at}.components.{c.Id}.paths.{prod}: must be a relative path without '..'.");
                }
                foreach (var dep in c.DependsOn)
                    if (p.Components.All(x => x.Id != dep)) e.Add($"{at}.components.{c.Id}: dependsOn unknown component '{dep}'.");
            }
            foreach (var g in p.Generated)
                if (g.Action is not ("block" or "exclude")) e.Add($"{at}.generated '{g.Pattern}': action must be block or exclude.");
            foreach (var (pn, prof) in p.Validation)
                foreach (var s in prof.Steps)
                    if (s.Run.Count == 0) e.Add($"{at}.validation.{pn}.{s.Id}: 'run' must list the executable and its arguments.");
        }
        return e;
    }
}
