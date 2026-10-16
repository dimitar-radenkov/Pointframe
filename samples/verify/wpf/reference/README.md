# Reference verification spec

Copy `verify.json` to the project `.pointframe/verify.json`, then run `pointframe verify run` from the project directory. The scenario checks required-name handling and a successful add, with a negative control for the status text.

`skill/verify-wpf-sample/` is the golden example of what the `create-desktop-verification-skill` skill writes for this app: a `verify-wpf-sample` project skill with one feature file. Copy it to `.claude/skills/` or `.agents/skills/` to use it.
