Imports System.Collections.Concurrent
Imports System.Timers
Imports Current.PluginApi

<PluginMetadata("Event Aggregator", "1.1", "Nasuno",
                "Manages event subscriptions and instantly dispatches events. Also provides optional spatial zone mouse enter/leave events.")>
Public Class EventAggregatorPlugin
    Implements IPlugin

    ' =========================================================
    ' ==  CORE EVENT AGGREGATOR (ORIGINAL BEHAVIOR, PRIMARY) ==
    ' =========================================================

    ' Stores all event subscriptions by event type name.
    ' Each event type name maps to a list of callbacks (subscribers) to invoke when that event is published.
    Private ReadOnly _subscriptions As New ConcurrentDictionary(Of String, List(Of Action(Of Object)))()

    ' Registers this plugin as the global event aggregator.
    Public Sub Execute(api As ICurrentApi) Implements IPlugin.Execute
        PluginLocator.Register("EventAggregator", Me)
        Console.WriteLine("[EventAggregatorPlugin] Registered as global event aggregator.")

        ' Start optional polling loop for spatial zones (secondary behavior)
        StartPolling(api)
    End Sub

    ' Publish an event: eventTypeName and data object (must be created by publisher)
    ' - eventTypeName: The string name representing the type of the event.
    ' - eventData: The event object carrying event data.
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
                    Console.WriteLine($"[EventAggregatorPlugin] Error in event callback: {ex.Message}")
                End Try
            Next
        End If
    End Sub

    ' Allows any plugin to subscribe to an event type by name.
    ' - eventTypeName: The string name representing the type of the event.
    ' - callback: Method to invoke when this event type is published.
    Public Sub Subscribe(eventTypeName As String, callback As Action(Of Object))
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return
        Dim list = _subscriptions.GetOrAdd(eventTypeName, New List(Of Action(Of Object))())
        SyncLock list
            list.Add(callback)
        End SyncLock
    End Sub

    ' Allows any plugin to unsubscribe from an event type by name.
    ' - eventTypeName: The string name representing the type of the event.
    ' - callback: The callback method to remove from the subscription list.
    ' Returns True if the callback was found and removed, False otherwise.
    Public Function Unsubscribe(eventTypeName As String, callback As Action(Of Object)) As Boolean
        If String.IsNullOrWhiteSpace(eventTypeName) OrElse callback Is Nothing Then Return False

        Dim list As List(Of Action(Of Object)) = Nothing
        If Not _subscriptions.TryGetValue(eventTypeName, list) Then Return False

        Dim removed As Boolean = False
        SyncLock list
            ' Find and remove the callback
            Dim delegateToRemove = list.FirstOrDefault(Function(d) d.Equals(callback))
            If delegateToRemove IsNot Nothing Then
                removed = list.Remove(delegateToRemove)

                ' If the list is now empty, consider removing the event type altogether
                If list.Count = 0 Then
                    Dim outList As List(Of Action(Of Object)) = Nothing
                    _subscriptions.TryRemove(eventTypeName, outList)
                End If
            End If
        End SyncLock

        Return removed
    End Function

    ' Unsubscribes all callbacks for a specific event type.
    ' - eventTypeName: The string name representing the type of the event.
    ' Returns True if the event type was found and all subscriptions cleared, False otherwise.
    Public Function UnsubscribeAll(eventTypeName As String) As Boolean
        If String.IsNullOrWhiteSpace(eventTypeName) Then Return False

        Dim list As List(Of Action(Of Object)) = Nothing
        Return _subscriptions.TryRemove(eventTypeName, list)
    End Function

    ' ======================================================================
    ' ==  OPTIONAL SPATIAL ZONE / MOUSE ENTER-LEAVE SUPPORT (SECONDARY)   ==
    ' ======================================================================

    ' Zones that have been explicitly registered for mouse enter/leave events.
    ' Key: zone.ID  Value: ISpatialZone reference
    Private ReadOnly _trackedZones As New ConcurrentDictionary(Of String, ISpatialZone)()

    ' Per-zone inside/outside state for the current observer ray.
    ' Key: zone.ID  Value: True if currently intersecting, False otherwise.
    Private ReadOnly _zoneInsideState As New ConcurrentDictionary(Of String, Boolean)()

    ' Timer for polling observer/zones
    Private _pollTimer As Timer

    ' Event type names for spatial zone mouse events
    Private Const EventZoneMouseEnter As String = "SpatialZoneMouseEnter"
    Private Const EventZoneMouseLeave As String = "SpatialZoneMouseLeave"

    ' -------------------------------
    ' -- ZONE REGISTRATION (PUBLIC) -
    ' -------------------------------

    ' Called by plugins: "start tracking this zone for mouse enter/leave events"
    Public Sub RegisterZoneForMouseEvents(zone As ISpatialZone)
        If zone Is Nothing Then Return
        _trackedZones(zone.ID) = zone
        _zoneInsideState(zone.ID) = False ' initialize as outside
        Console.WriteLine($"[EventAggregatorPlugin] Now tracking mouse events for zone '{zone.ID}'.")
    End Sub

    ' Optional: stop tracking a particular zone
    Public Sub UnregisterZoneForMouseEvents(zone As ISpatialZone)
        If zone Is Nothing Then Return
        Dim removed As ISpatialZone = Nothing
        _trackedZones.TryRemove(zone.ID, removed)
        Dim dummy As Boolean
        _zoneInsideState.TryRemove(zone.ID, dummy)
        Console.WriteLine($"[EventAggregatorPlugin] Stopped tracking mouse events for zone '{zone.ID}'.")
    End Sub

    ' ---------------------------------
    ' -- POLLING / EVENT EMISSION    --
    ' ---------------------------------

    Private Sub StartPolling(api As ICurrentApi)
        ' Only start once
        If _pollTimer IsNot Nothing Then Return
        If api Is Nothing Then Return

        _pollTimer = New Timer(100) ' 100 ms; adjust as needed
        AddHandler _pollTimer.Elapsed,
            Sub(sender As Object, e As ElapsedEventArgs)
                Try
                    PollObserverAndZones(api)
                Catch ex As Exception
                    Console.WriteLine("[EventAggregatorPlugin] Error during spatial zone polling: " & ex.Message)
                End Try
            End Sub
        _pollTimer.AutoReset = True
        _pollTimer.Start()
        Console.WriteLine("[EventAggregatorPlugin] Started spatial zone mouse event polling.")
    End Sub

    Private Sub PollObserverAndZones(api As ICurrentApi)
        If api Is Nothing Then Return

        ' Get observer origin & unit vector from API
        Dim origin = api.GetObserverOrigin()
        Dim uv = api.GetObserverUnitVector()

        ' If unit vector is zero, skip (no meaningful direction)
        If uv.Item1 = 0 AndAlso uv.Item2 = 0 AndAlso uv.Item3 = 0 Then
            Return
        End If

        ' Process only zones that have been explicitly registered
        For Each kvp In _trackedZones
            Dim zoneId As String = kvp.Key
            Dim zone As ISpatialZone = kvp.Value
            If zone Is Nothing Then Continue For

            Dim intersects = RayIntersectsZoneAabb(origin, uv, zone.BoundingBoxAABB)

            Dim wasInside As Boolean = _zoneInsideState.GetOrAdd(zoneId, False)

            If intersects AndAlso Not wasInside Then
                _zoneInsideState(zoneId) = True
                PublishZoneEvent("Enter", zoneId, origin, uv)
            ElseIf Not intersects AndAlso wasInside Then
                _zoneInsideState(zoneId) = False
                PublishZoneEvent("Leave", zoneId, origin, uv)
            End If
        Next
    End Sub

    Private Function RayIntersectsZoneAabb(origin As (Integer, Integer, Integer),
                                           unit As (Double, Double, Double),
                                           bb As ((Integer, Integer, Integer), (Integer, Integer, Integer))) As Boolean
        Dim minX As Integer = bb.Item1.Item1
        Dim minY As Integer = bb.Item1.Item2
        Dim minZ As Integer = bb.Item1.Item3
        Dim maxX As Integer = bb.Item2.Item1
        Dim maxY As Integer = bb.Item2.Item2
        Dim maxZ As Integer = bb.Item2.Item3

        Dim ox As Double = origin.Item1
        Dim oy As Double = origin.Item2
        Dim oz As Double = origin.Item3

        Dim dx As Double = unit.Item1
        Dim dy As Double = unit.Item2
        Dim dz As Double = unit.Item3

        Dim tMin As Double = 0.0
        Dim tMax As Double = Double.PositiveInfinity

        Dim intersects As Boolean = True

        ' X axis
        If Math.Abs(dx) < Double.Epsilon Then
            If ox < minX OrElse ox > maxX Then
                intersects = False
            End If
        Else
            Dim invD As Double = 1.0 / dx
            Dim t1 As Double = (minX - ox) * invD
            Dim t2 As Double = (maxX - ox) * invD
            If t1 > t2 Then
                Dim tmp = t1 : t1 = t2 : t2 = tmp
            End If
            tMin = Math.Max(tMin, t1)
            tMax = Math.Min(tMax, t2)
            If tMax < tMin Then
                intersects = False
            End If
        End If

        ' Y axis
        If intersects Then
            If Math.Abs(dy) < Double.Epsilon Then
                If oy < minY OrElse oy > maxY Then
                    intersects = False
                End If
            Else
                Dim invD As Double = 1.0 / dy
                Dim t1 As Double = (minY - oy) * invD
                Dim t2 As Double = (maxY - oy) * invD
                If t1 > t2 Then
                    Dim tmp = t1 : t1 = t2 : t2 = tmp
                End If
                tMin = Math.Max(tMin, t1)
                tMax = Math.Min(tMax, t2)
                If tMax < tMin Then
                    intersects = False
                End If
            End If
        End If

        ' Z axis
        If intersects Then
            If Math.Abs(dz) < Double.Epsilon Then
                If oz < minZ OrElse oz > maxZ Then
                    intersects = False
                End If
            Else
                Dim invD As Double = 1.0 / dz
                Dim t1 As Double = (minZ - oz) * invD
                Dim t2 As Double = (maxZ - oz) * invD
                If t1 > t2 Then
                    Dim tmp = t1 : t1 = t2 : t2 = tmp
                End If
                tMin = Math.Max(tMin, t1)
                tMax = Math.Min(tMax, t2)
                If tMax < tMin Then
                    intersects = False
                End If
            End If
        End If

        ' Only count intersections in front of the origin
        If intersects AndAlso tMax < 0 Then
            intersects = False
        End If

        Return intersects
    End Function

    Private Sub PublishZoneEvent(eventType As String,
                                 zoneId As String,
                                 origin As (Integer, Integer, Integer),
                                 unit As (Double, Double, Double))

        ' Build the event payload as an anonymous object so consumers can
        ' treat it as an Object and access properties via CallByName.
        Dim payload As Object = New With {
            .ZoneId = zoneId,
            .EventType = eventType,              ' "Enter" or "Leave"
            .ObserverOrigin = origin,           ' (Integer, Integer, Integer)
            .ObserverUnitVector = unit          ' (Double, Double, Double)
        }

        Dim eventName As String = If(eventType = "Enter", EventZoneMouseEnter, EventZoneMouseLeave)
        Publish(eventName, payload)
        Console.WriteLine($"[EventAggregatorPlugin] Published {eventName} for zone '{zoneId}'.")
    End Sub

End Class