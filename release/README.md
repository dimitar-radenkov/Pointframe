# Alias refresh marker

The legacy CLI and MCP aliases are copied into desktop releases from the newest complete agent release. If the scheduled
alias lag check reports that they are stale, the coordinator opens a refresh pull request by changing
`release/refresh-aliases.marker`. That marker selects the desktop train and requires the normal desktop qualification.
The merge then publishes a desktop release with the existing versioned agent pair copied byte for byte into the aliases.

Do not commit the marker itself as part of this documentation change. A marker change belongs in a separate refresh pull
request so it gets its own version and qualification.
