
A lightweight pub/sub event bus with spatial-zone mouse enter/leave detection. 

---

&nbsp;&nbsp;Accessing the Plugin<br>
```vb
Dim aggregator = PluginLocator. Get(Of Object)("EventAggregator")
```

---

&nbsp;&nbsp;Core Events API

&nbsp;&nbsp;Subscribe<br>
```vb
Sub Subscribe(eventTypeName As String, callback As Action(Of Object))
```
Register a callback for a named event type.

&nbsp;&nbsp;Publish<br>
```vb
Sub Publish(eventTypeName As String, eventData As Object)
```
Dispatch an event to all subscribers of that type. 

&nbsp;&nbsp;Unsubscribe<br>
```vb
Function Unsubscribe(eventTypeName As String, callback As Action(Of Object)) As Boolean
```
Remove a specific callback.  Returns `True` if removed.

&nbsp;&nbsp;UnsubscribeAll<br>
```vb
Function UnsubscribeAll(eventTypeName As String) As Boolean
```
Clear all callbacks for a given event type.

---

&nbsp;&nbsp;Custom Event Example

**Publisher (detects change and fires event):**<br>
```vb
Dim agg = PluginLocator. Get(Of Object)("EventAggregator")
Dim lastStatus As String = ""

Sub CheckStatus(currentStatus As String)
    If currentStatus <> lastStatus Then
        lastStatus = currentStatus
        Dim payload = New With { . NewValue = currentStatus }
        CallByName(agg, "Publish", CallType.Method, "StatusChanged", payload)
    End If
End Sub
```

**Subscriber (reacts to event):**<br>
```vb
Dim agg = PluginLocator.Get(Of Object)("EventAggregator")

Dim onStatusChanged As Action(Of Object) = Sub(evt)
    Dim newVal = CStr(CallByName(evt, "NewValue", CallType.Get))
    Console.WriteLine($"Status changed to: {newVal}")
End Sub

CallByName(agg, "Subscribe", CallType. Method, "StatusChanged", onStatusChanged)
```

---

&nbsp;&nbsp;Spatial Zone Mouse Events

RegisterZoneForMouseEvents<br>
```vb
Sub RegisterZoneForMouseEvents(zone As ISpatialZone)
```
Start tracking a zone for enter/leave events. 

UnregisterZoneForMouseEvents<br>
```vb
Sub UnregisterZoneForMouseEvents(zone As ISpatialZone)
```
Stop tracking a zone.

&nbsp;&nbsp;Built-in Event Types

  Event Name               | Fired When 
---------------------------|------------
 `"SpatialZoneMouseEnter"` | Observer ray enters a tracked zone's AABB 
 `"SpatialZoneMouseLeave"` | Observer ray leaves a tracked zone's AABB 

Event Payload Properties

  Property             |  Type                         | Description 
-----------------------|-------------------------------|-------------
 `.ZoneId`             | `String`                      | ID of the zone 
 `.EventType`          | `String`                      | `"Enter"` or `"Leave"` 
 `.ObserverOrigin`     | `(Integer, Integer, Integer)` | Observer position 
 `.ObserverUnitVector` | `(Double, Double, Double)`    | Observer look direction 

&nbsp;&nbsp;Spatial Zone Example

**Register a zone for tracking:**<br>
```vb
Dim agg = PluginLocator. Get(Of Object)("EventAggregator")
Dim myZone = api.GetSpatialZone("MyButtonZone")

CallByName(agg, "RegisterZoneForMouseEvents", CallType.Method, myZone)
```

**Subscribe to enter/leave events:**<br>
```vb
Dim onEnter As Action(Of Object) = Sub(evt)
    Dim zoneId = CStr(CallByName(evt, "ZoneId", CallType.Get))
    If zoneId = "MyButtonZone" Then
        Console.WriteLine("Mouse entered MyButtonZone")
    End If
End Sub

Dim onLeave As Action(Of Object) = Sub(evt)
    Dim zoneId = CStr(CallByName(evt, "ZoneId", CallType.Get))
    If zoneId = "MyButtonZone" Then
        Console.WriteLine("Mouse left MyButtonZone")
    End If
End Sub

CallByName(agg, "Subscribe", CallType.Method, "SpatialZoneMouseEnter", onEnter)
CallByName(agg, "Subscribe", CallType.Method, "SpatialZoneMouseLeave", onLeave)
```

---

&nbsp;&nbsp;Notes

Polling interval:  100ms<br>
Zone detection uses ray-AABB intersection (forward rays only)<br>
Store your callback reference to unsubscribe later

---

https://github.com/nasuno/Holodeck<br>
https://github.com/nasuno/Holodeck_API<br>
https://github.com/nasuno/Plugin-Satellite-Cubes<br>
https://github.com/nasuno/Plugin-SpatialZone-Demo<br>
https://github.com/nasuno/Plugin-Menu-System
