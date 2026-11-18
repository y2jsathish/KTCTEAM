# KTC Dispute Management API: what the portal uses

Source: `src/Ktc.Web/Seed/KTC_REST_API.postman_collection.json` (805 requests). Every dispute route lives under
`api/v1/DisputeManagement/`; nothing else in the collection mentions disputes, chargebacks or claims. There are 41 routes.

**Important:** the collection has no response samples and no descriptions, and most write bodies are empty (`{}` or `true`).
Only two bodies are filled in: `Search` and `Dispute/{Id}/ChangeState`. So field names of a dispute record, the state numbers
and the customer-detail keys are **not known** until we see JSON from your KTC server. The portal reads fields tolerantly and
shows every raw field it receives (card numbers masked), and does not guess numbers.

## 1. Disputes (the records)

| Method | Route | Read / write | Body in the collection | Used by the portal |
|---|---|---|---|---|
| POST | `Search` | read (POST used as a query) | `DisputeSearch`, see below | **Yes**: the Disputes list |
| POST | `Search/Multiple` | read | an array of `DisputeSearch` | No (one search per business is enough) |
| GET | `{clientid}/ClientDisputes?statusfilter=&Numdays=` | read | none | No: the meaning of `statusfilter` is unknown |
| GET | `{groupid}/GroupDisputes?statusfilter=&Numdays=` | read | none | No: same |
| GET | `Dispute` | read (all disputes, no filter) | none | No: could be very large; Search is used instead |
| GET | `Dispute/{Id}` | read | none | **Yes**: details popup and read-back after a write |
| POST | `Dispute` | write: create | `{}` (shape unknown) | No |
| POST | `Dispute/Update` | write: update whole record | `true` (shape unknown) | No |
| PUT | `Dispute/{id}/CustomerDetails?forceupdate=` | write | `{}` (shape unknown) | No |
| PUT | `Dispute/{id}/ExtraData?forceupdate=` | write | `{}` (shape unknown) | No |
| POST | `Dispute/{Id}/ChangeState` | write | `{"Item1": 64, "Item2": "sample string 2"}` | **Yes**, behind `DisputeWrite:Enabled` (off) |
| POST | `Dispute/Close?disputeId=` | write | `{}` | **Yes**, behind `DisputeWrite:Enabled` (off) |
| DELETE | `Dispute?disputeId=` | write: delete | none | **Never** |

### Search body (`DisputeSearch`)

```json
{
  "StatusIDList": [1, 2],
  "SessionFieldFilters":      { "<STX field id>": { FieldFilter } },
  "TransactionFieldFilters":  { "<STX field id>": { FieldFilter } },
  "CustomerInfoFieldFilters": { "<customer field name>": { FieldFilter } },
  "ExtraDataFieldFilters":    { "<extra data name>": { FieldFilter } },
  "BusinessID": 1, "GroupID": 1,
  "Start": "2026-11-12T10:49:34Z", "End": "2026-11-12T10:49:34Z",
  "ClientName": "sample string 2"
}
```

`FieldFilter` = `{UnderlyingType, FromDateTime, ToDateTime, BoolValue, StringValue, FromDecimal, ToDecimal, FromInt, ToInt, LookupID}`.

What this tells us:
* A dispute is attached to an ATM **session** and **transaction** (session / transaction fields are keyed by STX field id,
  see `GET api/v1/STXField`), and carries **customer info** and **extra data** maps keyed by name.
* States are numbers (`StatusIDList`), listed by `GET DisputeState`.
* The portal sends: `StatusIDList` = every id from `GET DisputeState` (or the one state picked in the filter), `Start` / `End`
  from the date filter, `BusinessID` once per business (as the ticket search must), `GroupID` = the "All Clients" group, and
  empty filter maps. ATM and amount filters are applied by the portal on the results, because the STX field id for the amount
  is not known (a server-side amount filter would need `TransactionFieldFilters` with that id).

### ChangeState body

`{"Item1": <int>, "Item2": "<string>"}` is a .NET `Tuple<int,string>`. `Item1` is almost certainly the new state id (from
`GET DisputeState`). `Item2` is a string; the portal sends the user's reason in it. **Not confirmed**: please capture what KTC's
Windows client sends when you change a dispute's state (Fiddler or the KTC API log), as was done for ticket comments.

## 2. Reference data (read by the portal)

| Method | Route | Use |
|---|---|---|
| GET | `DisputeState` | state id to name, for the status column and filter. Cached 30 min. |
| GET | `DisputeState/{id}` | not used (the list is enough) |
| GET | `DisputeRule`, `DisputeRule/{id}` | shown read-only at the bottom of the Disputes page ("KTC dispute states and rules"), raw fields |

`POST DisputeState`, `DisputeState/Update`, `DELETE DisputeState`, and the same for `DisputeRule`, are configuration
writes and are **not** used.

## 3. Customer notifications (SMS / messages)

| Method | Route | Read / write | Used |
|---|---|---|---|
| GET | `SMSQueue`, `SMSQueue/{Id}` | read | **Yes**: "Notifications" tab of the popup, entries whose dispute id matches |
| POST | `SMSQueue/Disputes` (`[1,2]` = dispute ids) | unclear: may queue SMS for those disputes | **No** (could text customers) |
| POST | `SMSQueue`, `SMSQueue/Update`, `SMSQueue/Complete?sms_queue_id=`, DELETE `SMSQueue` | write | No |
| GET | `DisputeMessageTemplate`, `DisputeMessageTemplate/{id}` | read | No (configuration) |
| GET / POST / DELETE | `DisputeMessageMaping` (`transaction_type_id`, `state_id`, `template_id`) | config | No |
| GET | `DisputeNotificationLogEntries?start=&end=&columnname=` | read | No: `columnname` is unknown |

The template mapping (transaction type + state -> message template) shows that KTC sends the customer a message when a dispute
reaches a state. That is another reason the portal never calls SMS writes.

## 4. Not in the API

* **No dispute comment route.** Ticket comments go through `POST api/v1/Comment/User` with a table number (6 = Ticket). A
  table number for disputes is not known, so the portal does not add dispute comments. If the Windows client adds comments to
  disputes, a trace of that call is needed.
* No documents / attachments, no history route, no reason-code list, no report route. If a dispute record carries a history or
  comments field, the popup shows it.

## What we need from your KTC server

1. JSON of `GET api/v1/DisputeManagement/DisputeState` (state ids and names).
2. JSON of `GET api/v1/DisputeManagement/Dispute/{id}` for one open and one closed dispute (card numbers can be masked).
3. The answer of `POST api/v1/DisputeManagement/Search` with the portal's body (Live Monitoring > API Health shows errors).
4. A trace of the Windows client changing a dispute's state (to confirm `Item2`) and, if it exists, adding a comment.
5. A screenshot of the Windows client's dispute list and dispute details window, so the portal can match its layout.

## Portal settings

```json
"Disputes":     { "LookbackDays": 30, "MaxRows": 2000 },
"DisputeWrite": { "Enabled": false, "MinReasonLength": 10, "MaxReasonLength": 500 }
```

`DisputeWrite:Enabled` is off by default. With it off, the popup still shows the change-state / close form and runs every
check, but sends nothing. With it on, each attempt is audited (user, dispute, state, masked reason, KTC's answers), needs the
dispute number typed to confirm, and only counts as done when `GET Dispute/{Id}` reads back the new state (or closed).
