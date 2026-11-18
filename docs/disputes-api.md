# KTC Dispute Management API: what the portal uses

Sources:
* the Postman collection `src/Ktc.Web/Seed/KTC_REST_API.postman_collection.json` (805 requests; 41 under
  `api/v1/DisputeManagement/`, nothing else mentions disputes). It has no response samples, and only `Search` and `ChangeState`
  have request bodies;
* **screenshots of KTC's Windows client** (Disputes tab, Dispute Details, Change Dispute State) and the bank's answer that
  `statusfilter` 0 means all states;
* a **trace of KTC's Windows client** (Kalignite log `disputetraces.kxlog`, 4–18 Nov 2026, two KTC servers) covering the
  Disputes tab, the Dispute Details window, its Transaction button, Add Comment and the Dispute Management Rules dialog.

The trace logs each call's URL and request body, the response **size** and the .NET type it was read into, but **not the
response bodies**. So request shapes below are confirmed; record field names, state names and state numbers are still not.

## What the Windows client does (from the trace)

| Client action | Calls, in order |
|---|---|
| Start-up | `GET DisputeManagement/DisputeRule` (10,120 chars, `Dictionary<short, DisputeRules>`, 42 rules); `GET DisputeManagement/DisputeState` (478 chars, `Dictionary<byte, DisputeState>`, **9 states**; the second server has 2) |
| Disputes tab of a group | `GET DisputeManagement/2/GroupDisputes?statusfilter=0&Numdays=14` (`List<Disputes>`; group 2 = All Clients) |
| Disputes tab of one ATM | `GET DisputeManagement/5/ClientDisputes?statusfilter=0&Numdays=14` (1,022 chars with one dispute) |
| Changing the "Dispute states filter" | sends the picked state as `statusfilter`: **0 = All** (confirmed by the bank), otherwise the state's id. For ATM 5, whose one dispute was in state "All", `statusfilter=1` (Pending) came back empty (`[]`) |
| After the list | `POST TransactionData/TransactionIDs` `[42262]` (the disputes' transaction ids), `POST TransactionData/ExtraTransactionFields` `{"Value1":[transaction UUIDs]}`, `POST STXFieldLookup//Batch` |
| Open Dispute Details | `GET DisputeState`, `GET DisputeRule`, **`POST DisputeManagement/SMSQueue/Disputes` `[10325]`** (answers `Dictionary<long, SMSQueue>`, so it is a read), `GET DisputeMessageTemplate`. No `GET Dispute/{id}`: the window shows the record from the list |
| Transaction button | `GET TransactionData/42262`, whose record ends with `…;5;10325`, i.e. ClientID 5 and **DisputeID 10325**; then `SessionData/{id}`, `FieldsFromSession`, `FieldsFromTransaction`, `ClientTransactionCounters` |
| **Add comment** | `POST api/v1/Comment/User` `{"table":13,"rowID":10325,"kcComment":null,"strComment":"tesst","SendStatusParameter":5,"Confidential":false,"BusinessID":-1}` → `true`. KTC then raises `DisputeChange` for client 5 and the client re-reads `ClientDisputes`, which grew from 1,022 to 1,143 chars: **comments are part of the dispute record** |
| OK | no call |

Not seen in the trace: **change state, close, create, update, delete**, customer details and extra-data updates, the
`Search` routes, and `GET Dispute/{id}`. The client's roles name the permissions: `KTCRole_ViewDisputes`,
`KTCRole_UpdateDisputes`, `KTCRole_CloseDisputes`, `KTCRole_ConfigureDisputes`.

KTC's `DisputeManagement` configuration (`DefaultDisputeExtraData`) defines each dispute's extra data:
`Amount` (NUMBER), `EffectiveAmount` (NUMBER) and `TSN` (STRING). The portal reads the amount from there when the record
has no amount field. The user setting `DisputesExtraSTXFieldIDs` adds transaction-field columns to the client's Disputes tab.

## What the Windows client shows (screenshots)

* **Disputes tab** (on each ATM and on a group): "Dispute states filter" (All, Pending, Settled, False Alert, Pending Notes not
  Validated, New not closed, Test, 1, Resolved - Under the Limit - Notes not Validated: the 9 states of `GET DisputeState`) and
  "Number of days to show" (14). Columns: Dispute Id, Transaction Id, Created At, Client, Status, Dispute Rules, Last Comment, Closed
  ("Not Closed" while open). A new dispute's status is "All", so state 0 is both the filter's "all" and a real state.
* **Dispute Details**: tabs Dispute Information and Queued SMS. Dispute Id, Transaction Id (Open), Client Name, Dispute Rules, Dispute
  Status (Change), Added Time, Last updated on, Closed Time; Customer Info (customerName, customerTelephone, customerMobileNumber,
  customerEmail, customerCategory, availableBalance, rmName, rmContact, rmEmail); Extra Data (Amount, EffectiveAmount, TSN);
  Comments (User, Time, Comment; the first is "Dispute created automatically based on matching rule." by System); buttons Add
  Comment, Delete, OK.
* **Change Dispute State**: Old Record State, New Record State (the states without "All"), Enter a comment, Cancel / OK.

The portal follows these layouts on the Disputes page and on a new **Disputes tab of the ATM page**. It has no Delete button (the
portal never deletes disputes), and Close is not shown, because the client has no Close button.

## All routes, and what the portal does with them

| Method | Route | Read / write | Portal |
|---|---|---|---|
| GET | `{groupid}/GroupDisputes?statusfilter=&Numdays=` | read | **Disputes page** (All Clients group; statusfilter = the picked state, 0 = all; Numdays = "Number of days to show") |
| GET | `{clientid}/ClientDisputes?statusfilter=&Numdays=` | read | **ATM page Disputes tab**, details and read-back (the dispute's ATM, statusfilter 0) |
| GET | `Dispute/{Id}` | read | fallback only, when the ATM is not known |
| POST | `Search`, `Search/Multiple` | read | not used (the client does not use them) |
| GET | `Dispute` | read | not used |
| GET | `DisputeState`, `DisputeState/{id}` | read | **state names** (list cached 30 min) |
| GET | `DisputeRule`, `DisputeRule/{id}` | read | **set-up section** |
| GET | `DisputeMessageTemplate`, `…/{id}` | read | **set-up section** |
| POST | `SMSQueue/Disputes` `[ids]` | read | **Customer Notifications tab** |
| GET | `SMSQueue`, `SMSQueue/{Id}` | read | not used |
| GET | `DisputeNotificationLogEntries?start=&end=&columnname=` | read | not used (`columnname` unknown) |
| POST | `api/v1/Comment/User` (table 13) | write | **Add comment**, behind `DisputeWrite:Enabled` |
| POST | `Dispute/{Id}/ChangeState` `{"Item1": stateId, "Item2": comment}` | write | **Change Dispute State** dialog (comment required, state 0 not offered), behind `Enabled` **and** `AllowStateChange` (call not in trace) |
| POST | `Dispute/Close?disputeId=` | write | API only, not shown (the client has no Close button); behind `Enabled` and `AllowStateChange` |
| POST | `Dispute`, `Dispute/Update`; PUT `…/CustomerDetails`, `…/ExtraData` | write | not used (bodies unknown) |
| DELETE | `Dispute?disputeId=` | write | never |
| POST/DELETE | `SMSQueue`, `SMSQueue/Update`, `SMSQueue/Complete`, `DisputeState`, `DisputeRule`, `DisputeMessageTemplate`, `DisputeMessageMaping` | config / messaging writes | never |

## Card data

Card-number fields show at most the first 6 and last 4 digits; track, CVV, PIN-block and ICC/EMV fields are hidden at any
depth (customer details, extra data, nested JSON or XML); card numbers inside free text (reasons, comments) are masked, also
in what the portal sends and in the audit. The card filter compares only the last 4 digits.

## Still needed from your KTC server

1. The JSON of `GET api/v1/DisputeManagement/DisputeState` (to confirm the state ids behind the names, and that "All" is 0).
2. The JSON of `GET api/v1/DisputeManagement/5/ClientDisputes?statusfilter=0&Numdays=14` (the record's field names; the portal
   reads them tolerantly until then).
3. A trace of the client's **Change** (Change Dispute State dialog), to confirm the ChangeState call and body.

A Fiddler capture, or the KTC REST API's own log with bodies, gives all three.

## Portal settings

```json
"Disputes":     { "LookbackDays": 14, "MaxRows": 2000, "StateNames": {}, "DetailLookupDays": 365 },
"DisputeWrite": { "Enabled": false, "CommentTable": 13, "AllowStateChange": false, "MinReasonLength": 10, "MaxReasonLength": 500 }
```

`DisputeWrite:Enabled` is off by default. With it off, the popup still shows the form and runs every check, but sends nothing.
With it on, comments are sent exactly as the Windows client sends them, and a comment counts as added only when the dispute,
read back from KTC, carries it. Change state also needs `AllowStateChange`, because the trace did not show
that call; it requires a comment, like the client's dialog. Every attempt is audited with the user, the dispute and the masked text.
