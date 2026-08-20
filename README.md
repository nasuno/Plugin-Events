

<br><br>

![](https://s12.gifyu.com/images/bE6ku.gif)

<br><br>

A lightweight pub/sub event bus with spatial-zone mouse enter/leave detection. 

---

```
Event Aggregator Documentation;
```

# THE EVENT AGGREGATOR

Complete reference for the `Event Aggregator` plugin, v1.5.

Register it in `commands.ini` and launch it before anything that depends on it. It publishes itself to `PluginHub` under the key `"EventAggregator"`.

**Contents**

&nbsp;&nbsp;Getting Started · Calling It · Publish and Subscribe<br>
&nbsp;&nbsp;Owner-Keyed Subscriptions · Sticky Events · One-Shot · Mailboxes<br>
&nbsp;&nbsp;Zone Mouse Events · Generic Mouse Events · Observer Pose<br>
&nbsp;&nbsp;Host Topics · Diagnostics · Method Index · Traps

---
---

===<br>
&nbsp;&nbsp;Guide<br>
**`Getting Started`**

&nbsp;&nbsp;What It Is<br>
A publish/subscribe broker. One plugin publishes an event by name; every plugin subscribed to that name receives it. Neither knows the other exists.

It also polls the observer and publishes mouse enter, leave and click events for registered spatial zones.

&nbsp;&nbsp;Launching It<br>
```ini
[Commands]
events
Event Aggregator
```
It registers itself on `Execute` and starts its polling timer.

&nbsp;&nbsp;Acquiring It<br>
```vb
Dim agg As Object = PluginHub.Fetch(Of Object)("EventAggregator")
If agg Is Nothing Then Return    ' not loaded, or not loaded YET
```

**Load order is not guaranteed.** Fetch lazily and retry rather than fetching once in `Execute` and giving up:
```vb
Private agg As Object

Private Function Aggregator() As Object
    If agg Is Nothing Then agg = PluginHub.Fetch(Of Object)("EventAggregator")
    Return agg
End Function
```

&nbsp;&nbsp;The Golden Rule<br>
**A callback should only record and return.**

`Publish` is synchronous. Every subscriber runs on the publisher's thread, one after another, before `Publish` returns. A slow callback slows the publisher and everybody behind you in the list.

For anything weighty, use a **mailbox** — the aggregator enforces the rule for you.

&nbsp;&nbsp;The Second Rule<br>
**Use `SubscribeOwned` with a Guid-suffixed key.** One call at teardown sweeps everything you took. A stranded delegate is invisible at runtime; owner keys are how that whole class of bug is retired.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Guide<br>
**`Calling It`**

The aggregator is reached as `Object` through `PluginHub`. Two mechanisms, and you need both.

&nbsp;&nbsp;Calling a method — `PluginHub.Exec`<br>
```vb
PluginHub.Exec(agg, "Publish", "MyEvent", payload)
```
Returns `Nothing` rather than throwing if the target is `Nothing` or the method is missing. Handles `Optional` parameters, so a short argument list works.

&nbsp;&nbsp;Reading a payload field — `CallByName`<br>
Payloads are anonymous types. `Exec` calls methods; it does not read properties.
```vb
Dim row = CInt(CallByName(evt, "Row", CallType.Get))
```

&nbsp;&nbsp;Passing a callback<br>
An `AddressOf` must be cast to `Action(Of Object)` or reflection cannot match the parameter:
```vb
PluginHub.Exec(agg, "Subscribe", "MyEvent",
               CType(AddressOf OnMyEvent, Action(Of Object)))
```

&nbsp;&nbsp;Return values<br>
`Exec` returns `Object`. Cast it:
```vb
Dim swept = CInt(PluginHub.Exec(agg, "UnsubscribeAllFor", ownerKey))
```

&nbsp;&nbsp;A working shape<br>
```vb
Private ReadOnly ownerKey As String = "MyPlugin:" & Guid.NewGuid().ToString()
Private agg As Object

Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute
    agg = PluginHub.Fetch(Of Object)("EventAggregator")
    If agg Is Nothing Then Return

    PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "MyEvent",
                   CType(AddressOf OnMyEvent, Action(Of Object)), False)
End Sub

Private Sub OnMyEvent(evt As Object)
    ' record and return
End Sub

Private Sub Teardown()
    PluginHub.Exec(agg, "UnsubscribeAllFor", ownerKey)
End Sub
```


<br><br><br><br>


# PUBLISH AND SUBSCRIBE

===<br>
&nbsp;&nbsp;Method<br>
**`Publish`**

&nbsp;&nbsp;Signature<br>
```vb
Sub Publish(eventTypeName As String, eventData As Object)
```

&nbsp;&nbsp;Purpose<br>
Delivers `eventData` to every callback subscribed to `eventTypeName`.

&nbsp;&nbsp;**Synchronous.** When `Publish` returns, every subscriber has run — on **your** thread, in subscription order. A slow subscriber is your problem.

&nbsp;&nbsp;Example Usage<br>
```vb
PluginHub.Exec(agg, "Publish", "MyPlugin.ThingHappened",
               New With {.Id = 42, .Name = "thing"})
```

&nbsp;&nbsp;Notes<br>
**Ignored silently if `eventData` is `Nothing`** or the name is blank. There is no way to publish a bare signal — send a payload, even a trivial one.<br>
A throwing subscriber is caught and logged; the remaining subscribers still run.<br>
The list is **copied** before dispatch, so a subscriber may subscribe or unsubscribe from inside a callback without corrupting the walk. Its effect lands on the next publish.<br>
No subscribers means no work.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`Subscribe`**

&nbsp;&nbsp;Signature<br>
```vb
Sub Subscribe(eventTypeName As String, callback As Action(Of Object))
```

&nbsp;&nbsp;Purpose<br>
Registers a callback for an event name.

```vb
PluginHub.Exec(agg, "Subscribe", "MyEvent",
               CType(AddressOf OnMyEvent, Action(Of Object)))
```

&nbsp;&nbsp;**Prefer `SubscribeOwned`.** A plain `Subscribe` must be undone by holding the exact delegate and calling `Unsubscribe` with it. Owner keys remove that bookkeeping entirely.

&nbsp;&nbsp;Notes<br>
Ignored silently on a blank name or a `Nothing` callback.<br>
Subscribing twice with the same callback registers it **twice**; it will fire twice per publish.<br>
Event names are **case-sensitive** — the subscription store uses the default comparer, unlike the diagnostics, which are case-insensitive.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Methods<br>
**`Unsubscribe`** · **`UnsubscribeAll`**

&nbsp;&nbsp;Signatures<br>
```vb
Function Unsubscribe(eventTypeName As String, callback As Action(Of Object)) As Boolean
Function UnsubscribeAll(eventTypeName As String) As Boolean
```

&nbsp;&nbsp;`Unsubscribe`<br>
Removes one callback. Returns `True` if one came off.

**You must pass the same delegate you subscribed with.** A fresh `AddressOf` of the same method may not compare equal — store the delegate in a field, or use `SubscribeOwned` and forget the problem.

&nbsp;&nbsp;`UnsubscribeAll`<br>
Removes **every** subscriber for a name, including other plugins'. Returns `True` if the name existed.

**Rarely the right call.** It is indiscriminate. Use `UnsubscribeAllFor(ownerKey)` to remove only your own.

&nbsp;&nbsp;Notes<br>
An event name with no subscribers left is dropped from the registry.


<br><br><br><br>


# OWNER-KEYED SUBSCRIPTIONS

===<br>
&nbsp;&nbsp;Guide<br>
**`Why Owner Keys Exist`**

A subscription is a delegate held by the broker. Once taken, nothing at runtime shows you it is there. Forget one and it fires for the life of the process, holding your object alive.

An owner key is a ledger. Subscribe under a key, and one call at teardown sweeps everything taken under it.

&nbsp;&nbsp;**One key per plugin INSTANCE, with a Guid.**
```vb
Private ReadOnly ownerKey As String = "MyPlugin:" & Guid.NewGuid().ToString()
```
A key per **class** means a second instance — or a relaunch of the same command, which starts another thread on the same instance — sweeps its sibling's subscriptions.

&nbsp;&nbsp;The Shape<br>
```vb
' Take as many as you like under one key
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "EventA",
               CType(AddressOf OnA, Action(Of Object)), False)
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "EventB",
               CType(AddressOf OnB, Action(Of Object)), False)

' One line retires all of them
PluginHub.Exec(agg, "UnsubscribeAllFor", ownerKey)
```

Owned subscriptions route through the ordinary `Subscribe`, so there is one dispatch path and no second delivery mechanism.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`SubscribeOwned`**

&nbsp;&nbsp;Signature<br>
```vb
Sub SubscribeOwned(ownerKey As String, eventTypeName As String,
                   callback As Action(Of Object),
                   Optional replaySticky As Boolean = False)
```

&nbsp;&nbsp;Parameters<br>
  Name             | Description
-------------------|-------------
 `ownerKey`        | Your instance key. One per plugin instance, Guid-suffixed
 `eventTypeName`   | The event to subscribe to
 `callback`        | Cast with `CType(AddressOf X, Action(Of Object))`
 `replaySticky`    | If `True` and a retained value stands for this event, deliver it **now**

&nbsp;&nbsp;`replaySticky`<br>
For state-shaped events. A plugin loading after the state was published learns it at once instead of waiting for the next change.

**The replay runs on YOUR thread**, at subscribe time — not the publisher's. Do not assume publish-thread affinity for the first call.

```vb
PluginHub.Exec(agg, "SubscribeOwned", ownerKey,
               HostEvents.ZoneLayoutTopic("Reader"),
               CType(AddressOf OnLayout, Action(Of Object)), True)
```

&nbsp;&nbsp;Notes<br>
Ignored silently on a blank key, blank name or `Nothing` callback.<br>
`replaySticky` does nothing if no value is retained for that event.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`UnsubscribeAllFor`**

&nbsp;&nbsp;Signature<br>
```vb
Function UnsubscribeAllFor(ownerKey As String) As Integer
```

&nbsp;&nbsp;Purpose<br>
Removes every subscription registered under the key. Returns how many actually came off.

```vb
Dim swept = CInt(PluginHub.Exec(agg, "UnsubscribeAllFor", ownerKey))
```

&nbsp;&nbsp;Notes<br>
Returns `0` on an unknown key — safe to call unconditionally.<br>
A count lower than what you subscribed means some were already removed by hand. Harmless.<br>
Idempotent: the ledger is taken out of the registry **before** the unsubscribes run, so a concurrent second sweep on the same key finds nothing rather than double-walking.<br>
Logs the count to the console.


<br><br><br><br>


# STICKY EVENTS

===<br>
&nbsp;&nbsp;Guide<br>
**`Retained State`**

A sticky event answers *"what is it NOW"* as well as *"it just changed."* The last payload is retained, and a subscriber arriving mid-session can be handed it immediately.

&nbsp;&nbsp;When to use it<br>
  Shape           | Example                              | Use
------------------|--------------------------------------|-----
 **State**        | active mode, current selection, a layout | `PublishSticky`
 **Occurrence**   | a click, an entity created           | `Publish`

Replaying a stale click to a newcomer would be a lie. **Retention is opt-in at the publish side, per event name.**

&nbsp;&nbsp;One retained value per name<br>
If several publishers share one topic, the retained value is whichever published last — and a newcomer is told about that one. For per-thing state, put the thing in the topic name:
```vb
"MyPlugin.State/" & thingId
```


<br><br><br><br>


===<br>
&nbsp;&nbsp;Methods<br>
**`PublishSticky`** · **`SubscribeSticky`** · **`GetSticky`** · **`ClearSticky`**

&nbsp;&nbsp;Signatures<br>
```vb
Sub PublishSticky(eventTypeName As String, eventData As Object)
Sub SubscribeSticky(eventTypeName As String, callback As Action(Of Object))
Function GetSticky(eventTypeName As String) As Object
Sub ClearSticky(eventTypeName As String)
```

&nbsp;&nbsp;`PublishSticky`<br>
Retains the payload, **then** dispatches through the ordinary path. Retaining first matters: a subscriber that reacts to this publish by calling `SubscribeSticky` on the same name must find the value already standing.

&nbsp;&nbsp;`SubscribeSticky`<br>
Subscribe plus immediate replay if a value stands. **Not owner-keyed** — prefer `SubscribeOwned(..., replaySticky:=True)`, which does the same and is sweepable.

&nbsp;&nbsp;`GetSticky`<br>
Reads the retained value **without subscribing**. `Nothing` if none stands. The poll-shaped door for a one-time answer.
```vb
Dim state = PluginHub.Exec(agg, "GetSticky", "MyPlugin.Mode")
If state IsNot Nothing Then ' ...
```

&nbsp;&nbsp;`ClearSticky`<br>
Retires a retained value. Subscriptions are untouched.

**Call this when your publisher departs**, or its last truth is replayed for ever to plugins that arrive after it is gone.

&nbsp;&nbsp;Notes<br>
`PublishSticky` ignores a `Nothing` payload, exactly as `Publish` does — so a `Nothing` never clears a retained value. Use `ClearSticky`.<br>
A replayed callback is guarded by the same try/catch as the publish loop.


<br><br><br><br>


# ONE-SHOT

===<br>
&nbsp;&nbsp;Method<br>
**`SubscribeOnce`**

&nbsp;&nbsp;Signature<br>
```vb
Sub SubscribeOnce(eventTypeName As String, callback As Action(Of Object))
```

&nbsp;&nbsp;Purpose<br>
Fires once, then removes itself. For waiting on a single occurrence — a handshake, a first frame, a readiness signal.

```vb
PluginHub.Exec(agg, "SubscribeOnce", "OtherPlugin.Ready",
               CType(AddressOf OnReady, Action(Of Object)))
```

&nbsp;&nbsp;Notes<br>
It unsubscribes **before** invoking your callback, so a re-publish from inside cannot fire it twice.<br>
**At-least-once, not exactly-once, under true concurrency.** `Publish` dispatches a copy of the list, so two publishes racing can each hold the wrapper. Single-threaded publishers see exactly-once.<br>
**Not owner-keyed.** If it never fires, it is never swept. For anything that might not arrive, use `SubscribeOwned` and unsubscribe yourself.


<br><br><br><br>


# MAILBOXES

===<br>
&nbsp;&nbsp;Guide<br>
**`Pull-Side Delivery`**

&nbsp;&nbsp;Purpose<br>
The golden rule made law. The inline callback for a mailbox is written by the aggregator and does nothing but enqueue, so a publisher pays one enqueue per mailbox, always. **A mailbox consumer cannot slow a publisher, by construction.**

&nbsp;&nbsp;No pump thread<br>
Draining is **pull** — your thread, your cadence. The broker stays synchronous, and the ordering contract is sharpened: when `Publish` returns, every inline subscriber has run **and** every mailbox holds the event.

&nbsp;&nbsp;Two modes, chosen at creation<br>
  Mode       | Behaviour
-------------|-----------
 `"FIFO"`    | Bounded queue, every event kept in order. Over capacity the **oldest** drops and a counter ticks
 `"LATEST"`  | One slot, newest wins. For state-shaped topics where only the current value matters

`LATEST` is the natural pull-side partner of `PublishSticky`.

&nbsp;&nbsp;The Shape<br>
```vb
PluginHub.Exec(agg, "CreateMailbox", "myBox", "LATEST")
PluginHub.Exec(agg, "SubscribeMailbox", "myBox", "SomeEvent", True)

' On your own timer, on your own thread
Dim latest = PluginHub.Exec(agg, "TakeLatest", "myBox")
If latest IsNot Nothing Then Process(latest)

' Teardown
PluginHub.Exec(agg, "RemoveMailbox", "myBox")
```


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`CreateMailbox`**

&nbsp;&nbsp;Signature<br>
```vb
Function CreateMailbox(mailboxId As String, mode As String,
                       Optional capacity As Integer = 256) As Boolean
```

&nbsp;&nbsp;Parameters<br>
  Name          | Description
----------------|-------------
 `mailboxId`    | Your identifier. IDs are **case-insensitive**
 `mode`         | `"FIFO"` or `"LATEST"`, case-insensitive
 `capacity`     | FIFO only. `LATEST` is one slot by nature

&nbsp;&nbsp;Returns<br>
`False` on a blank ID, an unknown mode, a non-positive capacity, or an **ID already in use**. State is left untouched — the seam refuses rather than guessing.

```vb
Dim ok = CBool(PluginHub.Exec(agg, "CreateMailbox", "myBox", "FIFO", 64))
```


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`SubscribeMailbox`**

&nbsp;&nbsp;Signature<br>
```vb
Function SubscribeMailbox(mailboxId As String, eventTypeName As String,
                          Optional replaySticky As Boolean = False) As Boolean
```

&nbsp;&nbsp;Purpose<br>
Routes a topic into a mailbox. You supply no callback — the enqueue is authored by the aggregator, which is what fixes the inline cost.

&nbsp;&nbsp;Returns<br>
`False` on an unknown mailbox ID or a blank event name.

&nbsp;&nbsp;Notes<br>
One mailbox may take **many** topics; drain order interleaves by arrival.<br>
`replaySticky` lands a retained value in the box at subscribe time, so the first drain already knows the standing state.<br>
The mailbox's internal subscriptions are owner-keyed internally, so `RemoveMailbox` sweeps them with the same machinery you would use yourself.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Methods (Draining)<br>
**`TakeAll`** · **`TakeLatest`** · **`DrainMailbox`**

&nbsp;&nbsp;Signatures<br>
```vb
Function TakeAll(mailboxId As String) As List(Of Object)
Function TakeLatest(mailboxId As String) As Object
Function DrainMailbox(mailboxId As String, handler As Action(Of Object)) As Integer
```

&nbsp;&nbsp;`TakeAll`<br>
Everything pending, in arrival order, on your thread. An **empty list** when nothing stands or the ID is unknown — never `Nothing`.

&nbsp;&nbsp;`TakeLatest`<br>
The standing value, cleared on take. `Nothing` when empty or unknown.

&nbsp;&nbsp;`DrainMailbox`<br>
Calls your handler per pending item on your thread, each guarded by the same try/catch as the publish loop. Returns the count handled.

&nbsp;&nbsp;**Both takes are mode-agnostic**<br>
  Call         | On a FIFO box                                   | On a LATEST box
---------------|-------------------------------------------------|------------------
 `TakeAll`     | everything, in order                            | the slot as a one-element list
 `TakeLatest`  | drains to the newest and returns it; **the older entries are counted as dropped** | the slot

```vb
For Each evt In CType(PluginHub.Exec(agg, "TakeAll", "myBox"), List(Of Object))
    Process(evt)
Next

PluginHub.Exec(agg, "DrainMailbox", "myBox",
               CType(AddressOf Process, Action(Of Object)))
```


<br><br><br><br>


===<br>
&nbsp;&nbsp;Method<br>
**`RemoveMailbox`**

&nbsp;&nbsp;Signature<br>
```vb
Function RemoveMailbox(mailboxId As String) As Integer
```

Full teardown: sweeps the box's internal subscriptions, then forgets the box and its contents. Returns the number of subscriptions swept. `0` on an unknown ID.

**Undrained contents are discarded.** Drain first if you care.


<br><br><br><br>


# ZONE MOUSE EVENTS

===<br>
&nbsp;&nbsp;Guide<br>
**`How Zone Events Work`**

The host affords the observer's pose by **pull** only. The aggregator polls it every **100ms** and, for each zone you have registered, tests the observer ray against that zone's `BoundingBoxAABB`.

- Ray now hits, did not before → **Enter**
- Ray no longer hits, did before → **Leave**

Clicks are separate: the host notifies the aggregator, which publishes a generic click **always**, then tests the registered zones and publishes a zone click for the **first** one hit.

&nbsp;&nbsp;Registration is required<br>
An unregistered zone produces no events, however it is drawn.

&nbsp;&nbsp;**Unregister on teardown.** The tracking dictionary holds your `ISpatialZone` reference and polls it for the life of the process.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Methods<br>
**`RegisterZoneForMouseEvents`** · **`UnregisterZoneForMouseEvents`**

&nbsp;&nbsp;Signatures<br>
```vb
Sub RegisterZoneForMouseEvents(zone As ISpatialZone)
Sub UnregisterZoneForMouseEvents(zone As ISpatialZone)
```

```vb
Dim zone = api.CreateSpatialZone("MyZone")
PluginHub.Exec(agg, "RegisterZoneForMouseEvents", zone)

' teardown
PluginHub.Exec(agg, "UnregisterZoneForMouseEvents", zone)
```

&nbsp;&nbsp;Notes<br>
Tracked by `zone.ID`, so **any** adapter for that zone unregisters it — you need not keep the same reference.<br>
Registering the same ID twice replaces the entry and resets its inside/outside state to *outside*.<br>
A `Nothing` zone is ignored.<br>
A zone with no valid rectangle has a collapsed bounding box and produces no hits, so a parked zone falls silent on its own. A **disposed** zone still occupies the tracking dictionary — unregister it. The host's `Disposed = True` layout payload is your cue.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Events (Zone)<br>
**`SpatialZoneMouseEnter`** · **`SpatialZoneMouseLeave`**<br>
**`SpatialZoneMouseClickLeft`** · **`SpatialZoneMouseClickRight`**

&nbsp;&nbsp;Payloads<br>

Enter and Leave:
  Field                  | Type                          | Description
-------------------------|-------------------------------|-------------
 `ZoneId`                | `String`                      | The zone
 `EventType`             | `String`                      | `"Enter"` or `"Leave"`
 `ObserverOrigin`        | `(Integer, Integer, Integer)` | Observer position
 `ObserverUnitVector`    | `(Double, Double, Double)`    | Facing direction

Click:
  Field                  | Type                          | Description
-------------------------|-------------------------------|-------------
 `ZoneId`                | `String`                      | The zone hit
 `ObserverOrigin`        | `(Integer, Integer, Integer)` | Observer position
 `ObserverUnitVector`    | `(Double, Double, Double)`    | Facing direction

```vb
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "SpatialZoneMouseClickLeft",
               CType(AddressOf OnZoneClick, Action(Of Object)), False)

Private Sub OnZoneClick(evt As Object)
    Dim id = CStr(CallByName(evt, "ZoneId", CallType.Get))
End Sub
```

&nbsp;&nbsp;**A zone click does not say WHERE inside the zone**<br>
There is no row or column. If you need that, take it from the **generic** click event's `Row` and `Col`, which the host resolves from world coordinates, or work it out yourself from the pose and the zone's `BoundingBoxAABB`.

&nbsp;&nbsp;Notes<br>
**Only the first zone hit** publishes a zone click; the scan stops there. With overlapping zones, which one wins is unspecified — the tracking dictionary has no ordering.<br>
Enter and Leave are edge-triggered per zone, so a still observer inside a zone produces no traffic.<br>
No hover events are published when the observer's unit vector is `(0, 0, 0)`. **Leave is not synthesised** in that case: a zone the observer was inside stays marked inside until the ray returns.<br>
Event names are subscribed as string literals; the aggregator does not export constants for them.


<br><br><br><br>


# GENERIC MOUSE EVENTS

===<br>
&nbsp;&nbsp;Events<br>
**`MouseClickLeft`** · **`MouseClickRight`**

&nbsp;&nbsp;Purpose<br>
Published on **every** click, whether or not a zone was hit.

&nbsp;&nbsp;Payload<br>
  Field                  | Type                          | Description
-------------------------|-------------------------------|-------------
 `Panel`                 | `Object`                      | The `PanelType` clicked, or `Nothing` if no cell was found
 `Row`                   | `Integer`                     | Grid row on that panel
 `Col`                   | `Integer`                     | Grid column
 `HitCell`               | `Boolean`                     | `False` when the click matched no grid cell
 `ObserverOrigin`        | `(Integer, Integer, Integer)` | Observer position
 `ObserverUnitVector`    | `(Double, Double, Double)`    | Facing direction

```vb
Private Sub OnClick(evt As Object)
    If Not CBool(CallByName(evt, "HitCell", CallType.Get)) Then Return
    Dim row = CInt(CallByName(evt, "Row", CallType.Get))
    Dim col = CInt(CallByName(evt, "Col", CallType.Get))
End Sub
```

&nbsp;&nbsp;**Check `HitCell` before trusting `Panel`, `Row` or `Col`.** When it is `False`, `Panel` is `Nothing` and the indices are `0`.

&nbsp;&nbsp;Ordering<br>
The generic click is published **before** the zone click. A subscriber to both hears the generic one first.

&nbsp;&nbsp;Notes<br>
`Row` and `Col` are 1-based panel grid indices, resolved by the host from the clicked world coordinate.<br>
`NotifyMouseClickLeft` and `NotifyMouseClickRight` are the host's entry points. **Plugins do not call them.**


<br><br><br><br>


# OBSERVER POSE

===<br>
&nbsp;&nbsp;Event<br>
**`Host.ObserverPose`**

&nbsp;&nbsp;Purpose<br>
Where the observer is and what he faces. Published because `ICurrentApi` affords gaze by pull alone — somebody must ask, and the aggregator has been asking since v1.3 for its own zone crossings.

&nbsp;&nbsp;Payload<br>
  Field                  | Type                          | Description
-------------------------|-------------------------------|-------------
 `ObserverOrigin`        | `(Integer, Integer, Integer)` | Position
 `ObserverUnitVector`    | `(Double, Double, Double)`    | Facing direction

```vb
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "Host.ObserverPose",
               CType(AddressOf OnPose, Action(Of Object)), False)
```

&nbsp;&nbsp;**Named for what it carries, not when it fires**<br>
It is **not a tick** and must never be used as one. Counting these for a cadence builds a timer out of somebody else's poll interval, and it breaks silently the day that interval changes.

&nbsp;&nbsp;**Published only on a change**<br>
The pose is compared with the last published one and a repeat is not sent. A still observer causes **no traffic whatever**, so you need not carry your own comparison to avoid pointless work.

&nbsp;&nbsp;Notes<br>
A lost gaze is still a pose change, and its subscribers are told — the unit vector arrives as `(0, 0, 0)`.<br>
Published from the poll thread, not a plugin thread.<br>
Not sticky: a late subscriber hears nothing until the observer next moves. Call `api.GetObserverOrigin()` yourself for a starting value.


<br><br><br><br>


# HOST TOPICS

===<br>
&nbsp;&nbsp;Guide<br>
**`Events the Host Publishes Through the Aggregator`**

The host publishes through whatever instance is registered as `"EventAggregator"`. It uses the ordinary `Publish` and `PublishSticky`, so these behave exactly like any plugin's events.

&nbsp;&nbsp;`SpatialZoneLayoutChanged`<br>
Raised when a zone completes a wrap, loses its rectangle, or is disposed. **Not** raised when `FirstVisibleLine` is assigned.

Two topics, same payload:
```vb
HostEvents.SpatialZoneLayoutChanged     ' every zone, plain
HostEvents.ZoneLayoutTopic(zoneId)      ' one zone, published sticky
```

Build the names with `HostEvents`, never by hand. Full payload and semantics are in the **Host API** documentation.

&nbsp;&nbsp;**Publishing is synchronous, and this one runs inside a `Text` assignment**<br>
Your handler executes on the thread that assigned `zone.Text`, before the assignment returns.

  Do not, from that handler | Because
----------------------------|---------
 Assign `Text` to any zone   | it publishes again — recursion
 Move a margin               | the host refuses to recurse and logs it
 Do heavy work               | you are holding up the plugin that assigned the text

Use a `LATEST` mailbox for anything weighty:
```vb
PluginHub.Exec(agg, "CreateMailbox", "layout", "LATEST")
PluginHub.Exec(agg, "SubscribeMailbox", "layout",
               HostEvents.ZoneLayoutTopic("MyZone"), True)
```

&nbsp;&nbsp;Notes<br>
The host does not create the aggregator. If none is registered, it publishes nothing and does not throw.<br>
The per-zone sticky value is **retired** on disposal, not replaced, so a plugin subscribing later is not handed the obituary of a zone it never knew.


<br><br><br><br>


# DIAGNOSTICS

===<br>
&nbsp;&nbsp;Methods<br>
**`ReportSubscriptionCounts`** · **`ReportOwnedCounts`** · **`ReportStickyNames`**<br>
**`ReportMailboxDepths`** · **`ReportMailboxDrops`**

&nbsp;&nbsp;Signatures<br>
```vb
Function ReportSubscriptionCounts() As Dictionary(Of String, Integer)
Function ReportOwnedCounts() As Dictionary(Of String, Integer)
Function ReportStickyNames() As List(Of String)
Function ReportMailboxDepths() As Dictionary(Of String, Integer)
Function ReportMailboxDrops() As Dictionary(Of String, Long)
```

&nbsp;&nbsp;What Each Exposes<br>
  Method                       | Reads                          | The bug it catches
-------------------------------|--------------------------------|--------------------
 `ReportSubscriptionCounts`    | subscribers per event name     | A count climbing by one per file operation is a stranded lifecycle subscriber
 `ReportOwnedCounts`           | subscriptions per owner key    | Who is holding what. A key that outlives its plugin was never swept
 `ReportStickyNames`           | names carrying a retained value | A retained value whose publisher is gone
 `ReportMailboxDepths`         | pending items per mailbox      | A depth pinned at capacity is a consumer that forgot its drain loop
 `ReportMailboxDrops`          | cumulative drops per mailbox   | A climbing count is the same fault, or a capacity set too low

```vb
Dim counts = CType(PluginHub.Exec(agg, "ReportSubscriptionCounts"),
                   Dictionary(Of String, Integer))
For Each kvp In counts
    Console.WriteLine($"{kvp.Key}: {kvp.Value}")
Next
```

&nbsp;&nbsp;Notes<br>
All are **snapshots**. Nothing changes under you.<br>
The dictionaries are case-insensitive, though subscription lookup is case-**sensitive** — two names differing only in case appear as one row here but are two distinct topics.<br>
For a `LATEST` mailbox, an overwritten slot **counts as a drop**. A steady drop count on a `LATEST` box is normal, not a fault.


<br><br><br><br>


# REFERENCE

===<br>
&nbsp;&nbsp;Method Index

  Method                                          | Returns   | Purpose
--------------------------------------------------|-----------|---------
 **Core** |||
 `Publish(name, data)`                            | —         | Deliver to all subscribers, synchronously
 `Subscribe(name, cb)`                            | —         | Register a callback
 `Unsubscribe(name, cb)`                          | `Boolean` | Remove one callback
 `UnsubscribeAll(name)`                           | `Boolean` | Remove **everyone's** callbacks for a name
 **Owned** |||
 `SubscribeOwned(key, name, cb, [replaySticky])`  | —         | Subscribe and record under a key
 `UnsubscribeAllFor(key)`                         | `Integer` | Sweep everything under a key
 **Sticky** |||
 `PublishSticky(name, data)`                      | —         | Retain, then publish
 `SubscribeSticky(name, cb)`                      | —         | Subscribe plus immediate replay
 `GetSticky(name)`                                | `Object`  | Read the retained value
 `ClearSticky(name)`                              | —         | Retire the retained value
 **One-shot** |||
 `SubscribeOnce(name, cb)`                        | —         | Fires once, removes itself
 **Mailboxes** |||
 `CreateMailbox(id, mode, [capacity])`            | `Boolean` | `"FIFO"` or `"LATEST"`
 `SubscribeMailbox(id, name, [replaySticky])`     | `Boolean` | Route a topic into a mailbox
 `TakeAll(id)`                                    | `List`    | Drain everything, in order
 `TakeLatest(id)`                                 | `Object`  | The newest, cleared on take
 `DrainMailbox(id, handler)`                      | `Integer` | Handler per pending item
 `RemoveMailbox(id)`                              | `Integer` | Full teardown
 **Zones** |||
 `RegisterZoneForMouseEvents(zone)`               | —         | Start hover and click tracking
 `UnregisterZoneForMouseEvents(zone)`             | —         | Stop it
 **Diagnostics** |||
 `ReportSubscriptionCounts()`                     | `Dict`    | Subscribers per event
 `ReportOwnedCounts()`                            | `Dict`    | Subscriptions per owner key
 `ReportStickyNames()`                            | `List`    | Names carrying a retained value
 `ReportMailboxDepths()`                          | `Dict`    | Pending per mailbox
 `ReportMailboxDrops()`                           | `Dict`    | Cumulative drops per mailbox
 **Host only — do not call** |||
 `NotifyMouseClickLeft(...)` `NotifyMouseClickRight(...)` | — | The host's click entry points


<br><br><br><br>


===<br>
&nbsp;&nbsp;Event Index

  Name                          | Published by | Sticky | Payload
--------------------------------|--------------|--------|---------
 `MouseClickLeft`               | aggregator   | no     | `Panel`, `Row`, `Col`, `HitCell`, pose
 `MouseClickRight`              | aggregator   | no     | as above
 `SpatialZoneMouseEnter`        | aggregator   | no     | `ZoneId`, `EventType`, pose
 `SpatialZoneMouseLeave`        | aggregator   | no     | as above
 `SpatialZoneMouseClickLeft`    | aggregator   | no     | `ZoneId`, pose
 `SpatialZoneMouseClickRight`   | aggregator   | no     | as above
 `Host.ObserverPose`            | aggregator   | no     | `ObserverOrigin`, `ObserverUnitVector`
 `SpatialZoneLayoutChanged`     | **host**     | no     | see Host API
 `SpatialZoneLayoutChanged/<id>`| **host**     | **yes**| see Host API

"pose" means `ObserverOrigin` and `ObserverUnitVector`.


<br><br><br><br>


===<br>
&nbsp;&nbsp;Cheatsheet

```vb
Private ReadOnly ownerKey As String = "MyPlugin:" & Guid.NewGuid().ToString()
Private agg As Object = PluginHub.Fetch(Of Object)("EventAggregator")

' PUBLISH
PluginHub.Exec(agg, "Publish", "MyEvent", New With {.Id = 1})
PluginHub.Exec(agg, "PublishSticky", "MyState", New With {.Mode = "edit"})
PluginHub.Exec(agg, "ClearSticky", "MyState")

' SUBSCRIBE
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "MyEvent",
               CType(AddressOf OnEvent, Action(Of Object)), False)
PluginHub.Exec(agg, "SubscribeOwned", ownerKey, "MyState",
               CType(AddressOf OnState, Action(Of Object)), True)   ' replay
PluginHub.Exec(agg, "SubscribeOnce", "Ready",
               CType(AddressOf OnReady, Action(Of Object)))

' READ A PAYLOAD
Dim row = CInt(CallByName(evt, "Row", CallType.Get))

' MAILBOX
PluginHub.Exec(agg, "CreateMailbox", "box", "LATEST")
PluginHub.Exec(agg, "SubscribeMailbox", "box", "MyState", True)
Dim latest = PluginHub.Exec(agg, "TakeLatest", "box")

' ZONES
PluginHub.Exec(agg, "RegisterZoneForMouseEvents", zone)
PluginHub.Exec(agg, "UnregisterZoneForMouseEvents", zone)

' TEARDOWN
PluginHub.Exec(agg, "UnsubscribeAllFor", ownerKey)
PluginHub.Exec(agg, "RemoveMailbox", "box")
```


<br><br><br><br>


===<br>
&nbsp;&nbsp;Traps

- **Heavy work in a callback.** `Publish` is synchronous; you slow the publisher and everyone behind you. Use a mailbox.
- **An owner key without a Guid.** A relaunched or rebuilt instance sweeps its sibling's subscriptions.
- **Forgetting `UnsubscribeAllFor`.** A stranded delegate fires for the life of the process and holds your object alive.
- **Forgetting `UnregisterZoneForMouseEvents`.** The 100ms poll keeps your `ISpatialZone` reference for ever.
- **`Publish` with a `Nothing` payload.** Ignored silently. There is no bare signal.
- **Expecting `Publish(name, Nothing)` to clear a sticky value.** Use `ClearSticky`.
- **`Unsubscribe` with a fresh `AddressOf`.** May not compare equal to the delegate you subscribed. Store it, or use owner keys.
- **`UnsubscribeAll`.** Removes other plugins' subscriptions too. You almost always want `UnsubscribeAllFor`.
- **Subscribing the same callback twice.** It fires twice.
- **Treating `Host.ObserverPose` as a timer.** It fires on change only.
- **Trusting `Row` or `Col` without checking `HitCell`.**
- **Expecting a zone click to say where inside the zone.** It does not.
- **Assuming a zone click for every overlapping zone.** Only the first hit publishes.
- **Case-mismatched event names.** Subscription lookup is case-sensitive; the diagnostics are not.
- **`SubscribeOnce` on an event that may never fire.** It is never swept.
- **`CreateMailbox` with an ID already in use.** Returns `False`; check it.
- **Assuming a mailbox drains itself.** There is no pump thread. Drain on your own cadence.
- **Assuming the aggregator is loaded.** Fetch lazily and retry.
