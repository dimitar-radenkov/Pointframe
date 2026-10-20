param([string]$Root, [switch]$NoProbe)
# Fixture environment hook: four facts, or "unknown" for each when KB_PROBE_FAULT is set (the selftest's probe fault).
$names = @('fixture tool', 'fixture lock', 'fixture installs', 'git worktrees')
foreach ($name in $names)
{
    if ($NoProbe) { "- ${name}: not probed (-NoProbe)" }
    elseif ($env:KB_PROBE_FAULT) { "- ${name}: unknown" }
    else { "- ${name}: ok" }
}