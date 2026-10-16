# Add a contact

## Sub-features

- Adding a named contact appends it to the list and sets the status to "Added <name>".
- Adding with an empty name changes nothing in the list and sets the status to "Name is required".

## How to get to it (user POV)

The window opens on the Contacts form. A user types a name in the box at the top, then presses Add.
There is no other screen.

## Driving it with Pointframe

Scenario `add-contact` in `.pointframe/verify.json`; run it with `pointframe verify run`.

| Control | Selector |
|---|---|
| Name box | `automationId` `nameBox` (role `Edit`) |
| Add button | `automationId` `addButton` |
| Status line | `automationId` `statusText` (role `Text`) |
| Contact list | `automationId` `contactList` |
| A contact row | role `ListItem`, name = the contact's text |

Criteria it proves:

- C1: a non-empty name is added and the status confirms it ("Added Ada Lovelace", and a `ListItem`
  named "Ada Lovelace" exists).
- C2: an empty name is rejected without adding an item ("Name is required").

The negative control expects the wrong status text ("Not added Ada Lovelace") with `expectFailure`; the
check passes only because that text is rejected.

## Gotchas

- The list row's role is `ListItem`, not `list item`; the name is the row text.
- `C2` runs first on purpose: the first `invoke` happens with the box empty. Entering the name before it
  would make the rejection impossible to see.
- The broken variant (`-p:SampleBroken=true`) still sets the status, so the `ListItem` check is the one
  that fails there.
