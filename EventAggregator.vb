Imports System.Collections.Concurrent
Imports System.Timers
Imports Current.PluginApi

' EventAggregator v1.5
' v1.3: publish/subscribe, generic mouse clicks, spatial zone enter/leave/click.
' v1.4: owner-keyed subscriptions with one-call sweep (UnsubscribeAllFor),
'   sticky (retained) events for state-shaped publishes, one-shot subscriptions,
'   and subscription diagnostics. Dispatch path unchanged: every v1.4 subscribe
'   variant routes through the v1.3 Subscribe, so owned/sticky/once subscribers
'   are ordinary entries in the same lists - one delivery mechanism, no forks.
' v1.5: mailbox subscriptions - bounded pull-side delivery decoupling. The broker
'   stays synchronous and draining is pull-side, so a mailbox consumer cannot slow
'   a publisher.

<PluginMetadata("Event Aggregator", "1.5", "Nasuno",
                "Manages event subscriptions and dispatches events. Provides generic mouse click events and spatial zone mouse enter/leave/click events. v1.4: owner-keyed subscriptions with one-call sweep, sticky (retained) events, one-shot subscriptions, and subscription diagnostics. v1.5: mailbox subscriptions (bounded pull-side delivery decoupling).")>
Public Class EventAggregatorPlugin
    Implements IPlugin

    Private ReadOnly _subscriptions As New ConcurrentDictionary(Of String, List(Of Action(Of Object)))()
    Private _api As ICurrentApi

    Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute
        _api = api
        PluginHub.Register("EventAggregator", Me)
        Console.WriteLine("[EventAggregator] Registered as global event aggregator.")
        StartPolling()
    End Sub

    ' =====================
    ' == PUBLISH/SUBSCRIBE
    ' =====================

    Public Sub Publish(eventTypeName As String, eventData As Object)
        If eventData Is Nothing OrElse String.IsNullOrWhiteSpace(eventTypeName) Then Return
        Dim list As List(Of Action(Of Object)) = Nothing
        If _subscriptions.TryGetValue(eventTypeName, list) Then
            Dim copy As List(Of Action(Of Object))
            SyncLock list
                copy = New List(Of Action(Of Object))(list)
            End SyncLock
            For Each cb In copy
                Try
                    cb(eventData)
                Catch ex As Exception
                    Console.WriteLine($"[EventAggregator] Callback error: {ex.Message}")
                End Try
            Next
        End If
    End Sub

    Public Sub Subscribe(eventTypeName As String, callback As Action(Of Object))
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return
        Dim list = _subscriptions.GetOrAdd(eventTypeName, Function(k) New List(Of Action(Of Object))())
        SyncLock list
            list.Add(callback)
        End SyncLock
    End Sub

    Public Function Unsubscribe(eventTypeName As String, callback As Action(Of Object)) As Boolean
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return False
        Dim list As List(Of Action(Of Object)) = Nothing
        If Not _subscriptions.TryGetValue(eventTypeName, list) Then Return False

        Dim removed As Boolean = False
        SyncLock list
            Dim target = list.FirstOrDefault(Function(d) d.Equals(callback))
            If target IsNot Nothing Then
                removed = list.Remove(target)
                If list.Count = 0 Then
                    Dim outList As List(Of Action(Of Object)) = Nothing
                    _subscriptions.TryRemove(eventTypeName, outList)
                End If
            End If
        End SyncLock
        Return removed
    End Function

    Public Function UnsubscribeAll(eventTypeName As String) As Boolean
        If String.IsNullOrWhiteSpace(eventTypeName) Then Return False
        Dim list As List(Of Action(Of Object)) = Nothing
        Return _subscriptions.TryRemove(eventTypeName, list)
    End Function






















#Region "v1.5 Mailbox Subscriptions"

    ' The golden rule ("a callback should only record and return") made LAW rather
    ' than convention: the inline callback for a mailbox subscription is written by
    ' the aggregator itself and does nothing but enqueue. A publisher pays one
    ' enqueue per mailbox, always - a mailbox consumer CANNOT slow a publisher,
    ' by construction.
    ' Deliberately NO pump thread: the broker stays synchronous. Draining is pull -
    ' the consumer's thread, the consumer's cadence. The ordering contract is thus
    ' preserved and sharpened: when Publish returns, every inline subscriber has
    ' run AND every mailbox holds the event.
    ' Two modes, chosen at creation:
    '   "FIFO"   - bounded queue, every event kept in order. Over capacity, the
    '              OLDEST drops and a counter ticks - a mailbox never drained is a
    '              visible number, not unbounded growth.
    '   "LATEST" - one slot, newest wins. For state-shaped topics (cursor moved,
    '              mode changed) where only the current value matters; the natural
    '              pull-side partner of PublishSticky.
    Private NotInheritable Class Mailbox
        Public ReadOnly Id As String
        Public ReadOnly Mode As String
        Public ReadOnly Capacity As Integer
        Public ReadOnly Fifo As New ConcurrentQueue(Of Object)
        Public ReadOnly SlotLock As New Object()
        Public LatestSlot As Object = Nothing
        Public Dropped As Long = 0
        Public Sub New(id As String, mode As String, capacity As Integer)
            Me.Id = id : Me.Mode = mode : Me.Capacity = capacity
        End Sub
    End Class

    Private ReadOnly _mailboxes As New ConcurrentDictionary(Of String, Mailbox)(StringComparer.OrdinalIgnoreCase)

    ' Owner key for a mailbox's internal subscriptions. Reuses the v1.4 owned
    ' machinery whole: RemoveMailbox IS UnsubscribeAllFor on this key - one sweep
    ' mechanism, no second teardown story.
    Private Shared Function MailboxOwnerKey(mailboxId As String) As String
        Return "mailbox:" & mailboxId
    End Function

    ' mode: "FIFO" or "LATEST". capacity applies to FIFO only (LATEST is one slot
    ' by nature). False on blank id, unknown mode, non-positive capacity, or an
    ' existing id - refuse whole, state untouched, the seam's refuse-don't-guess idiom.
    Public Function CreateMailbox(mailboxId As String, mode As String,
                                  Optional capacity As Integer = 256) As Boolean
        If String.IsNullOrWhiteSpace(mailboxId) Then Return False
        Dim m = If(mode, "").ToUpperInvariant()
        If m <> "FIFO" AndAlso m <> "LATEST" Then Return False
        If capacity <= 0 Then Return False
        Return _mailboxes.TryAdd(mailboxId, New Mailbox(mailboxId, m, capacity))
    End Function

    ' Route a topic into a mailbox. The enqueue callback is authored HERE - the
    ' consumer never supplies inline code, so the inline cost is fixed by
    ' construction. replaySticky: a retained value lands in the mailbox at
    ' subscribe time, so the first drain already knows the standing state.
    ' One mailbox may take many topics; drain order interleaves by arrival.
    Public Function SubscribeMailbox(mailboxId As String, eventTypeName As String,
                                     Optional replaySticky As Boolean = False) As Boolean
        Dim box As Mailbox = Nothing
        If Not _mailboxes.TryGetValue(mailboxId, box) Then Return False
        If String.IsNullOrWhiteSpace(eventTypeName) Then Return False
        Dim deposit As Action(Of Object) = Sub(evt) DepositTo(box, evt)
        SubscribeOwned(MailboxOwnerKey(mailboxId), eventTypeName, deposit, replaySticky)
        Return True
    End Function

    Private Shared Sub DepositTo(box As Mailbox, evt As Object)
        If box.Mode = "LATEST" Then
            SyncLock box.SlotLock
                If box.LatestSlot IsNot Nothing Then box.Dropped += 1   ' overwritten = dropped
                box.LatestSlot = evt
            End SyncLock
            Return
        End If
        box.Fifo.Enqueue(evt)
        ' Bounded: shed the OLDEST past capacity. Newest-wins shedding matches the
        ' LATEST philosophy - stale events are the right ones to lose.
        Dim spill As Object = Nothing
        While box.Fifo.Count > box.Capacity AndAlso box.Fifo.TryDequeue(spill)
            Threading.Interlocked.Increment(box.Dropped)
        End While
    End Sub

    ' FIFO drain: everything pending, in arrival order, on YOUR thread. Empty list
    ' when nothing stands or the id is unknown. On a LATEST box, returns the slot
    ' as a one-element list (and clears it) so a mode-agnostic consumer still works.
    Public Function TakeAll(mailboxId As String) As List(Of Object)
        Dim o As New List(Of Object)
        Dim box As Mailbox = Nothing
        If Not _mailboxes.TryGetValue(mailboxId, box) Then Return o
        If box.Mode = "LATEST" Then
            Dim one = TakeLatest(mailboxId)
            If one IsNot Nothing Then o.Add(one)
            Return o
        End If
        Dim evt As Object = Nothing
        While box.Fifo.TryDequeue(evt)
            o.Add(evt)
        End While
        Return o
    End Function

    ' LATEST read: the standing value, cleared on take. Nothing when empty/unknown.
    ' On a FIFO box, drains to the newest and returns it (older entries counted
    ' dropped) - again mode-agnostic by construction.
    Public Function TakeLatest(mailboxId As String) As Object
        Dim box As Mailbox = Nothing
        If Not _mailboxes.TryGetValue(mailboxId, box) Then Return Nothing
        If box.Mode = "LATEST" Then
            SyncLock box.SlotLock
                Dim v = box.LatestSlot
                box.LatestSlot = Nothing
                Return v
            End SyncLock
        End If
        Dim last As Object = Nothing, evt As Object = Nothing
        While box.Fifo.TryDequeue(evt)
            If last IsNot Nothing Then Threading.Interlocked.Increment(box.Dropped)
            last = evt
        End While
        Return last
    End Function

    ' Convenience drain: handler per pending item, caller's thread, each guarded by
    ' the same try/catch policy as the Publish loop. Returns items handled.
    Public Function DrainMailbox(mailboxId As String, handler As Action(Of Object)) As Integer
        If handler Is Nothing Then Return 0
        Dim items = TakeAll(mailboxId)
        For Each evt In items
            Try
                handler(evt)
            Catch ex As Exception
                Console.WriteLine($"[EventAggregator] Mailbox drain error: {ex.Message}")
            End Try
        Next
        Return items.Count
    End Function

    ' Full teardown: sweeps the box's internal subscriptions via the v1.4 owned
    ' mechanism, then forgets the box and its contents. Returns subscriptions swept.
    Public Function RemoveMailbox(mailboxId As String) As Integer
        Dim box As Mailbox = Nothing
        If Not _mailboxes.TryRemove(mailboxId, box) Then Return 0
        Return UnsubscribeAllFor(MailboxOwnerKey(mailboxId))
    End Function

    ' Diagnostics: pending depth and cumulative drops per mailbox. A depth pinned
    ' at capacity, or a climbing drop count, is the never-drained signature - the
    ' consumer that forgot its loop, made visible as a number.
    Public Function ReportMailboxDepths() As Dictionary(Of String, Integer)
        Dim d As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        For Each kvp In _mailboxes
            Dim box = kvp.Value
            d(kvp.Key) = If(box.Mode = "LATEST",
                            If(box.LatestSlot IsNot Nothing, 1, 0),
                            box.Fifo.Count)
        Next
        Return d
    End Function

    Public Function ReportMailboxDrops() As Dictionary(Of String, Long)
        Dim d As New Dictionary(Of String, Long)(StringComparer.OrdinalIgnoreCase)
        For Each kvp In _mailboxes
            d(kvp.Key) = kvp.Value.Dropped
        Next
        Return d
    End Function

#End Region



















#Region "v1.4 Owner-Keyed Subscriptions"

    ' Every subscription taken under an ownerKey is remembered here so ONE call can
    ' sweep them all at teardown. This exists because a stranded delegate is
    ' invisible at runtime - the CAD frustum leak was findable only by reading
    ' code. With owned subscription, lifecycle teardown is one line per plugin,
    ' not one stored handler per subscription: the leak class dies at the
    ' registry, not by vigilance.
    ' Key discipline (the caller's duty): one key per plugin INSTANCE, not per
    ' class - suffix a Guid - or a rebuilt instance sweeps a sibling's entries.
    Private ReadOnly _owned As New ConcurrentDictionary(Of String, List(Of (EventName As String, Callback As Action(Of Object))))()

    ' Subscribe AND record under ownerKey. Delegates to the v1.3 Subscribe so the
    ' dispatch path stays single. replaySticky: if the event has a retained value
    ' (see PublishSticky), deliver it to this callback immediately - a status bar
    ' late to the party still learns the current state.
    Public Sub SubscribeOwned(ownerKey As String, eventTypeName As String,
                              callback As Action(Of Object),
                              Optional replaySticky As Boolean = False)
        If String.IsNullOrWhiteSpace(ownerKey) Then Return
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return
        Subscribe(eventTypeName, callback)
        Dim list = _owned.GetOrAdd(ownerKey, Function(k) New List(Of (String, Action(Of Object)))())
        SyncLock list
            list.Add((eventTypeName, callback))
        End SyncLock
        If replaySticky Then ReplayStickyTo(eventTypeName, callback)
    End Sub

    ' The sweep. Removes every delegate registered under ownerKey; returns how
    ' many actually came off. A shortfall against what was subscribed means some
    ' were already Unsubscribed by hand - harmless, the hand-removal already did
    ' the work. Safe on unknown owners (returns 0). TryRemove FIRST: the owner's
    ' ledger leaves the registry before the unsubscribes run, so a concurrent
    ' second sweep on the same key finds nothing rather than double-walking.
    Public Function UnsubscribeAllFor(ownerKey As String) As Integer
        If String.IsNullOrWhiteSpace(ownerKey) Then Return 0
        Dim list As List(Of (String, Action(Of Object))) = Nothing
        If Not _owned.TryRemove(ownerKey, list) Then Return 0
        Dim removed = 0
        SyncLock list
            For Each entry In list
                If Unsubscribe(entry.Item1, entry.Item2) Then removed += 1
            Next
            list.Clear()
        End SyncLock
        Console.WriteLine($"[EventAggregator] Swept {removed} subscription(s) for owner '{ownerKey}'.")
        Return removed
    End Function

#End Region

#Region "v1.4 Sticky (Retained) Events"

    ' Last payload per sticky event name - the latest wins. A sticky event answers
    ' "what is it NOW" as well as "it just changed": built for state-shaped events
    ' (CAD.SessionModeChanged, active drawing, active layer) where a subscriber
    ' arriving mid-session must not wait for the next transition. Plain Publish
    ' remains correct for occurrence-shaped events (clicks, entity created) -
    ' replaying a stale click to a newcomer would be a lie, so retention is
    ' strictly opt-in at the PUBLISH side, per event name.
    Private ReadOnly _sticky As New ConcurrentDictionary(Of String, Object)()

    ' Retain, then dispatch through the ordinary path. Retain FIRST: a subscriber
    ' that reacts to this publish by calling SubscribeSticky on the same event
    ' must find the value already standing.
    Public Sub PublishSticky(eventTypeName As String, eventData As Object)
        If eventData Is Nothing OrElse String.IsNullOrWhiteSpace(eventTypeName) Then Return
        _sticky(eventTypeName) = eventData
        Publish(eventTypeName, eventData)
    End Sub

    ' Subscribe + immediate replay of the retained value, if one stands. The
    ' replay runs on the CALLER'S thread (subscribe time), not the publisher's -
    ' subscribers must not assume publish-thread affinity for their first call.
    Public Sub SubscribeSticky(eventTypeName As String, callback As Action(Of Object))
        Subscribe(eventTypeName, callback)
        ReplayStickyTo(eventTypeName, callback)
    End Sub

    ' Retire a retained value (e.g. the publisher departs and its old truth must
    ' not be replayed to newcomers). Subscriptions are untouched.
    Public Sub ClearSticky(eventTypeName As String)
        Dim dummy As Object = Nothing
        _sticky.TryRemove(eventTypeName, dummy)
    End Sub

    ' Read the retained value without subscribing - the poll-shaped door for
    ' consumers that want a one-time answer. Nothing when no value stands.
    Public Function GetSticky(eventTypeName As String) As Object
        Dim last As Object = Nothing
        _sticky.TryGetValue(eventTypeName, last)
        Return last
    End Function

    ' One guarded delivery, shared by SubscribeSticky and SubscribeOwned's replay
    ' flag - same try/catch policy as the Publish loop, so a throwing subscriber
    ' is contained identically on both paths.
    Private Sub ReplayStickyTo(eventTypeName As String, callback As Action(Of Object))
        Dim last As Object = Nothing
        If _sticky.TryGetValue(eventTypeName, last) Then
            Try
                callback(last)
            Catch ex As Exception
                Console.WriteLine($"[EventAggregator] Sticky replay error: {ex.Message}")
            End Try
        End If
    End Sub

#End Region

#Region "v1.4 One-Shot Subscription"

    ' Fires once, then removes itself. The holder array gives the wrapper a
    ' reference to itself for the self-unsubscribe - the stored-handler law
    ' satisfied internally. Unsubscribe runs BEFORE the callback so a re-publish
    ' from within it cannot fire the wrapper twice. Note the Publish loop
    ' dispatches a COPY of the list, so two publishes racing the same one-shot
    ' can each hold the wrapper; the Unsubscribe-first ordering makes the second
    ' invocation's removal a no-op but not its call - at-least-once, not
    ' exactly-once, under true concurrency. Single-threaded publishers (all
    ' current ones) see exactly-once.
    Public Sub SubscribeOnce(eventTypeName As String, callback As Action(Of Object))
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return
        Dim holder(0) As Action(Of Object)
        holder(0) = Sub(evt)
                        Unsubscribe(eventTypeName, holder(0))
                        callback(evt)
                    End Sub
        Subscribe(eventTypeName, holder(0))
    End Sub

#End Region

#Region "v1.4 Diagnostics"

    ' Live subscriber count per event name - a snapshot. The leak class this
    ' exposes: a count that climbs by one per file operation is a stranded
    ' lifecycle subscriber.
    Public Function ReportSubscriptionCounts() As Dictionary(Of String, Integer)
        Dim d As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        For Each kvp In _subscriptions
            SyncLock kvp.Value
                d(kvp.Key) = kvp.Value.Count
            End SyncLock
        Next
        Return d
    End Function

    ' Owned-subscription count per owner key - who is holding what.
    Public Function ReportOwnedCounts() As Dictionary(Of String, Integer)
        Dim d As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        For Each kvp In _owned
            SyncLock kvp.Value
                d(kvp.Key) = kvp.Value.Count
            End SyncLock
        Next
        Return d
    End Function

    ' Names carrying a retained value - lets a dashboard enumerate standing state.
    Public Function ReportStickyNames() As List(Of String)
        Return New List(Of String)(_sticky.Keys)
    End Function

#End Region

    ' ========================
    ' == GENERIC MOUSE EVENTS
    ' ========================

    Private Const EventMouseClickLeft As String = "MouseClickLeft"
    Private Const EventMouseClickRight As String = "MouseClickRight"

    ' ========================
    ' == SPATIAL ZONE EVENTS
    ' ========================

    Private Const EventZoneMouseEnter As String = "SpatialZoneMouseEnter"
    Private Const EventZoneMouseLeave As String = "SpatialZoneMouseLeave"
    Private Const EventZoneMouseClickLeft As String = "SpatialZoneMouseClickLeft"
    Private Const EventZoneMouseClickRight As String = "SpatialZoneMouseClickRight"

    Private ReadOnly _trackedZones As New ConcurrentDictionary(Of String, ISpatialZone)()
    Private ReadOnly _zoneInsideState As New ConcurrentDictionary(Of String, Boolean)()
    Private _pollTimer As Timer

    Public Sub RegisterZoneForMouseEvents(zone As ISpatialZone)
        If zone Is Nothing Then Return
        _trackedZones(zone.ID) = zone
        _zoneInsideState(zone.ID) = False
        Console.WriteLine($"[EventAggregator] Tracking zone '{zone.ID}' for mouse events.")
    End Sub

    Public Sub UnregisterZoneForMouseEvents(zone As ISpatialZone)
        If zone Is Nothing Then Return
        Dim removed As ISpatialZone = Nothing
        _trackedZones.TryRemove(zone.ID, removed)
        Dim dummy As Boolean
        _zoneInsideState.TryRemove(zone.ID, dummy)
        Console.WriteLine($"[EventAggregator] Stopped tracking zone '{zone.ID}'.")
    End Sub

    ' ====================
    ' == CLICK HANDLING
    ' ====================

    Public Sub NotifyMouseClickLeft(panel As Object, row As Integer, col As Integer, hitCell As Boolean)
        HandleMouseClick(EventMouseClickLeft, EventZoneMouseClickLeft, panel, row, col, hitCell)
    End Sub

    Public Sub NotifyMouseClickRight(panel As Object, row As Integer, col As Integer, hitCell As Boolean)
        HandleMouseClick(EventMouseClickRight, EventZoneMouseClickRight, panel, row, col, hitCell)
    End Sub

    Private Sub HandleMouseClick(genericEvent As String, zoneEvent As String,
                                 panel As Object, row As Integer, col As Integer, hitCell As Boolean)
        If _api Is Nothing Then Return

        Dim origin = _api.GetObserverOrigin()
        Dim uv = _api.GetObserverUnitVector()

        ' Publish generic click event (always fires)
        PublishClickEvent(genericEvent, origin, uv, panel, row, col, hitCell)

        ' Early exit for zone checks if no valid ray
        If uv.Item1 = 0 AndAlso uv.Item2 = 0 AndAlso uv.Item3 = 0 Then Return

        ' Check zones and publish zone-specific events if hit
        For Each kvp In _trackedZones
            Dim zoneId = kvp.Key
            Dim zone = kvp.Value
            If zone Is Nothing Then Continue For

            If RayIntersectsZoneAabb(origin, uv, zone.BoundingBoxAABB) Then
                PublishZoneClickEvent(zoneEvent, zoneId, origin, uv)
                Exit For
            End If
        Next
    End Sub

    Private Sub PublishClickEvent(eventName As String,
                                  origin As (Integer, Integer, Integer),
                                  unit As (Double, Double, Double),
                                  panel As Object, row As Integer, col As Integer, hitCell As Boolean)
        Dim payload = New With {
            .Panel = panel,
            .Row = row,
            .Col = col,
            .HitCell = hitCell,
            .ObserverOrigin = origin,
            .ObserverUnitVector = unit
        }
        Publish(eventName, payload)
        Console.WriteLine($"[EventAggregator] Published {eventName}.")
    End Sub

    Private Sub PublishZoneClickEvent(eventName As String,
                                      zoneId As String,
                                      origin As (Integer, Integer, Integer),
                                      unit As (Double, Double, Double))
        Dim payload = New With {
            .ZoneId = zoneId,
            .ObserverOrigin = origin,
            .ObserverUnitVector = unit
        }
        Publish(eventName, payload)
        Console.WriteLine($"[EventAggregator] Published {eventName} for zone '{zoneId}'.")
    End Sub

    ' =================
    ' == HOVER POLLING
    ' =================

    Private Sub StartPolling()
        If _pollTimer IsNot Nothing Then Return
        If _api Is Nothing Then Return

        _pollTimer = New Timer(100)
        AddHandler _pollTimer.Elapsed, AddressOf OnPollTick
        _pollTimer.AutoReset = True
        _pollTimer.Start()
        Console.WriteLine("[EventAggregator] Started spatial zone polling.")
    End Sub

    Private Sub OnPollTick(sender As Object, e As ElapsedEventArgs)
        Try
            PollObserverAndZones()
        Catch ex As Exception
            Console.WriteLine($"[EventAggregator] Poll error: {ex.Message}")
        End Try
    End Sub

    Private Sub PollObserverAndZones()
        If _api Is Nothing Then Return

        Dim origin = _api.GetObserverOrigin()
        Dim uv = _api.GetObserverUnitVector()

        If uv.Item1 = 0 AndAlso uv.Item2 = 0 AndAlso uv.Item3 = 0 Then Return

        For Each kvp In _trackedZones
            Dim zoneId = kvp.Key
            Dim zone = kvp.Value
            If zone Is Nothing Then Continue For

            Dim intersects = RayIntersectsZoneAabb(origin, uv, zone.BoundingBoxAABB)
            Dim wasInside = _zoneInsideState.GetOrAdd(zoneId, False)

            If intersects AndAlso Not wasInside Then
                _zoneInsideState(zoneId) = True
                PublishHoverEvent("Enter", zoneId, origin, uv)
            ElseIf Not intersects AndAlso wasInside Then
                _zoneInsideState(zoneId) = False
                PublishHoverEvent("Leave", zoneId, origin, uv)
            End If
        Next
    End Sub

    Private Sub PublishHoverEvent(eventType As String,
                                  zoneId As String,
                                  origin As (Integer, Integer, Integer),
                                  unit As (Double, Double, Double))
        Dim payload = New With {
            .ZoneId = zoneId,
            .EventType = eventType,
            .ObserverOrigin = origin,
            .ObserverUnitVector = unit
        }

        Dim eventName = If(eventType = "Enter", EventZoneMouseEnter, EventZoneMouseLeave)
        Publish(eventName, payload)
        Console.WriteLine($"[EventAggregator] Published {eventName} for zone '{zoneId}'.")
    End Sub

    ' ======================
    ' == RAY-AABB INTERSECTION
    ' ======================

    Private Function RayIntersectsZoneAabb(origin As (Integer, Integer, Integer),
                                           unit As (Double, Double, Double),
                                           bb As ((Integer, Integer, Integer), (Integer, Integer, Integer))) As Boolean
        Dim minX = bb.Item1.Item1, minY = bb.Item1.Item2, minZ = bb.Item1.Item3
        Dim maxX = bb.Item2.Item1, maxY = bb.Item2.Item2, maxZ = bb.Item2.Item3

        Dim ox = CDbl(origin.Item1), oy = CDbl(origin.Item2), oz = CDbl(origin.Item3)
        Dim dx = unit.Item1, dy = unit.Item2, dz = unit.Item3

        Dim tMin = 0.0, tMax = Double.PositiveInfinity

        If Math.Abs(dx) < Double.Epsilon Then
            If ox < minX OrElse ox > maxX Then Return False
        Else
            Dim invD = 1.0 / dx
            Dim t1 = (minX - ox) * invD
            Dim t2 = (maxX - ox) * invD
            If t1 > t2 Then Dim tmp = t1 : t1 = t2 : t2 = tmp
            tMin = Math.Max(tMin, t1)
            tMax = Math.Min(tMax, t2)
            If tMax < tMin Then Return False
        End If

        If Math.Abs(dy) < Double.Epsilon Then
            If oy < minY OrElse oy > maxY Then Return False
        Else
            Dim invD = 1.0 / dy
            Dim t1 = (minY - oy) * invD
            Dim t2 = (maxY - oy) * invD
            If t1 > t2 Then Dim tmp = t1 : t1 = t2 : t2 = tmp
            tMin = Math.Max(tMin, t1)
            tMax = Math.Min(tMax, t2)
            If tMax < tMin Then Return False
        End If

        If Math.Abs(dz) < Double.Epsilon Then
            If oz < minZ OrElse oz > maxZ Then Return False
        Else
            Dim invD = 1.0 / dz
            Dim t1 = (minZ - oz) * invD
            Dim t2 = (maxZ - oz) * invD
            If t1 > t2 Then Dim tmp = t1 : t1 = t2 : t2 = tmp
            tMin = Math.Max(tMin, t1)
            tMax = Math.Min(tMax, t2)
            If tMax < tMin Then Return False
        End If

        If tMax < 0 Then Return False

        Return True
    End Function

End Class