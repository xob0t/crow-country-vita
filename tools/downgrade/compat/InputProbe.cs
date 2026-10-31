using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

// -crow-input (Diag.cs reads the flag): what the pad gives, from Unity's input to Rewired's maps and player 0's
// actions, as it changes, and every 5 s the joysticks Unity and Rewired see and player 0's maps. By reflection
// (CrowCompat doesn't reference Rewired), with its own Get, which also reads non-public members and puts the error
// in the log line in place of a value (Helpers.cs' ByName throws or gives null).
// Part of CrowCompat; see Compat2018.cs.
namespace CrowCompat
{
    static class InputProbe
    {
        static float lastReport = -10f;

        // Each frame of a -crow-input run (Diag.Update).
        internal static void Frame(float now)
        {
            LogInput();
            LogInputState();
            if (now - lastReport < 5f) return;
            lastReport = now;
            ReportInput();
        }

        static object Get(object target, string name)
        {
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null) return property.GetValue(target, null);
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field == null ? null : field.GetValue(target);
            }
            catch (Exception exception)
            {
                return "<" + exception.GetType().Name + ">";
            }
        }

        static string Count(object list)
        {
            var collection = list as System.Collections.ICollection;
            return collection == null ? "null" : collection.Count.ToString();
        }

        // -crow-input: Unity's joystick buttons (any joystick, then joysticks 1-2) as they go down,
        // and the keys and mouse buttons Unity reports, to tell input that never reaches Unity from
        // input that Rewired doesn't map.
        static void LogInput()
        {
            if (!Input.anyKeyDown) return;
            var down = new List<string>();
            for (KeyCode key = KeyCode.Backspace; key <= KeyCode.Joystick2Button19; key++)
                if (Enum.IsDefined(typeof(KeyCode), key) && Input.GetKeyDown(key)) down.Add(key.ToString());
            Debug.Log("CROW_INPUT down " + string.Join(" ", down.ToArray()));
        }

        // -crow-input, every frame, logged when it changes: Unity's raw axes past 0.2 (Joy1-4, axes 1-29,
        // as Rewired's Input Manager entries name them: JoyNAxisM is Unity's axis M-1 of joystick N) and
        // the joystick buttons held; each Rewired joystick's active elements (its hardware map's view);
        // player 0's active actions (what the game's PlayMaker actions read). Moving a stick shows which
        // of the three loses it.
        static string[] axisNames;
        static string lastUnity = "", lastElements = "", lastActions = "";
        static object[] actionIds;
        static string[] actionNames;
        static MethodInfo getAxisRaw;
        static readonly Dictionary<string, PropertyInfo> properties = new Dictionary<string, PropertyInfo>();

        static void LogInputState()
        {
            if (axisNames == null)
            {
                var names = new List<string>();
                for (int joystick = 1; joystick <= 4; joystick++)
                    for (int axis = 1; axis <= 29; axis++)
                    {
                        string name = "Joy" + joystick + "Axis" + axis;
                        try
                        {
                            Input.GetAxisRaw(name);
                            names.Add(name);
                        }
                        catch (ArgumentException)
                        {
                        }
                    }
                axisNames = names.ToArray();
                Debug.Log("CROW_INPUT probing " + axisNames.Length + " Unity axes");
            }
            var unity = new StringBuilder();
            foreach (string name in axisNames)
            {
                float value = Input.GetAxisRaw(name);
                if (Mathf.Abs(value) > 0.2f) unity.Append(' ').Append(name).Append('=').Append(value.ToString("0.0"));
            }
            for (KeyCode key = KeyCode.Joystick1Button0; key <= KeyCode.Joystick4Button19; key++)
                if (Input.GetKey(key)) unity.Append(' ').Append(key);
            Changed(ref lastUnity, "unity", unity.ToString());
            // Rewired reads Unity's input: with none there and nothing active at the last look, there
            // is nothing new to see, and the reflection below allocates (about 0.3 MB/s at 17 fps).
            if (lastUnity.Length == 0 && lastElements.Length == 0 && lastActions.Length == 0) return;

            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null || !Equals(GetStatic(reInput, "isReady"), true)) return;
            var elements = new StringBuilder();
            object controllers = GetStatic(reInput, "controllers");
            var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
            if (joysticks != null)
            {
                int index = 0;
                foreach (object joystick in joysticks)
                {
                    var axes = Cached(joystick, "Axes") as System.Collections.IEnumerable;
                    if (axes != null)
                        foreach (object axis in axes)
                        {
                            object value = Cached(axis, "value");
                            if (value is float && Mathf.Abs((float)value) > 0.2f)
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(axis)).Append('=').Append(((float)value).ToString("0.0"));
                        }
                    var buttons = Cached(joystick, "Buttons") as System.Collections.IEnumerable;
                    if (buttons != null)
                        foreach (object button in buttons)
                            if (Equals(Cached(button, "value"), true))
                                elements.Append(" j").Append(index).Append(':').Append(ElementName(button));
                    index++;
                }
            }
            Changed(ref lastElements, "rewired elements", elements.ToString());
            string served = Pad.Active();  // the pad reader's actions while it serves (Rewired's are stale then)
            if (served != null)
            {
                Changed(ref lastActions, "player0 actions (pad reader)", served);
                return;
            }

            object players = GetStatic(reInput, "players");
            object player = null;
            try
            {
                MethodInfo getPlayer = players == null ? null : players.GetType().GetMethod("GetPlayer", new[] { typeof(int) });
                player = getPlayer == null ? null : getPlayer.Invoke(players, new object[] { 0 });
            }
            catch (Exception)
            {
            }
            if (player == null) return;
            if (actionIds == null)
            {
                object mapping = GetStatic(reInput, "mapping");
                var actions = mapping == null ? null : Get(mapping, "Actions") as System.Collections.IEnumerable;
                var ids = new List<object>();
                var names = new List<string>();
                if (actions != null)
                    foreach (object action in actions)
                    {
                        ids.Add(Get(action, "id"));
                        names.Add(Get(action, "name") as string);
                    }
                actionIds = ids.ToArray();
                actionNames = names.ToArray();
                getAxisRaw = player.GetType().GetMethod("GetAxisRaw", new[] { typeof(int) });
                Debug.Log("CROW_INPUT probing " + actionIds.Length + " Rewired actions: " + string.Join(", ", actionNames));
            }
            if (getAxisRaw == null) return;
            var active = new StringBuilder();
            for (int i = 0; i < actionIds.Length; i++)
            {
                object value;
                try
                {
                    value = getAxisRaw.Invoke(player, new[] { actionIds[i] });
                }
                catch (Exception)
                {
                    continue;
                }
                if (value is float && Mathf.Abs((float)value) > 0.2f)
                    active.Append(" [").Append(actionNames[i]).Append("]=").Append(((float)value).ToString("0.0"));
            }
            Changed(ref lastActions, "player0 actions", active.ToString());
        }

        static void Changed(ref string last, string what, string now)
        {
            if (now == last) return;
            last = now;
            Debug.Log("CROW_INPUT " + what + ":" + (now.Length == 0 ? " none" : now));
        }

        static string ElementName(object element)
        {
            object identifier = Cached(element, "elementIdentifier");
            object name = identifier == null ? null : Cached(identifier, "name");
            return name as string ?? "?";
        }

        // Get with the property looked up once per type (LogInputState reads hundreds a frame).
        static object Cached(object target, string name)
        {
            Type type = target.GetType();
            string key = type.FullName + ":" + name;
            PropertyInfo property;
            if (!properties.TryGetValue(key, out property))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                try
                {
                    property = type.GetProperty(name, flags);
                }
                catch (AmbiguousMatchException)
                {
                    property = type.GetProperties(flags).FirstOrDefault(p => p.Name == name && p.GetIndexParameters().Length == 0);
                }
                properties[key] = property;
            }
            try
            {
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Unity's joystick names and Rewired's state (by reflection: CrowCompat doesn't reference Rewired).
        static void ReportInput()
        {
            var line = new StringBuilder("CROW_INPUT unity joysticks=[").Append(string.Join("|", Input.GetJoystickNames())).Append(']');
            Type reInput = Type.GetType("Rewired.ReInput, Rewired_Core");
            if (reInput == null)
            {
                Debug.Log(line.Append(" no Rewired.ReInput").ToString());
                return;
            }
            object ready = GetStatic(reInput, "isReady");
            line.Append(" rewired ready=").Append(ready);
            if (Equals(ready, true))
            {
                object controllers = GetStatic(reInput, "controllers");
                line.Append(" joystickCount=").Append(controllers == null ? "?" : Get(controllers, "joystickCount"));
                var joysticks = controllers == null ? null : Get(controllers, "Joysticks") as System.Collections.IEnumerable;
                if (joysticks != null)
                    foreach (object joystick in joysticks)
                        line.Append(" [").Append(Get(joystick, "name")).Append(" / ").Append(Get(joystick, "hardwareName"))
                            .Append(" id ").Append(Get(joystick, "id")).Append(" unityId ").Append(Get(joystick, "unityId"))
                            .Append(" axes ").Append(Count(Get(joystick, "Axes")))
                            .Append(" buttons ").Append(Count(Get(joystick, "Buttons"))).Append(']');
                object keyboard = controllers == null ? null : Get(controllers, "Keyboard");
                line.Append(" keyboard=").Append(keyboard == null ? "null" : Get(keyboard, "enabled"));
                object players = GetStatic(reInput, "players");
                var all = players == null ? null : Get(players, "AllPlayers") as System.Collections.IEnumerable;
                if (all != null)
                    foreach (object player in all)
                    {
                        object playerControllers = Get(player, "controllers");
                        line.Append(" player ").Append(Get(player, "name")).Append(" joysticks=")
                            .Append(playerControllers == null ? "?" : Get(playerControllers, "joystickCount"))
                            .Append(" hasKeyboard=").Append(playerControllers == null ? "?" : Get(playerControllers, "hasKeyboard"));
                        if (Equals(Get(player, "id"), 0)) Changed(ref lastMaps, "player0 maps", Maps(playerControllers));
                    }
            }
            Debug.Log(line.ToString());
        }

        static string lastMaps = "";

        // A player's controller maps: controller type and id, category, layout, hardware GUID (the
        // Dual Analog Gamepad template is 83b427e4, the PS Vita controller d2aef070), enabled, entries.
        static string Maps(object playerControllers)
        {
            object maps = playerControllers == null ? null : Get(playerControllers, "maps");
            if (maps == null) return " ?";
            System.Collections.IEnumerable all;
            try
            {
                MethodInfo getAllMaps = maps.GetType().GetMethods().FirstOrDefault(m =>
                    m.Name == "GetAllMaps" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                all = getAllMaps == null ? null : getAllMaps.Invoke(maps, null) as System.Collections.IEnumerable;
            }
            catch (Exception exception)
            {
                return " <" + exception.GetType().Name + ">";
            }
            if (all == null) return " ?";
            var line = new StringBuilder();
            foreach (object map in all)
            {
                string guid = Convert.ToString(Get(map, "hardwareGuid"));
                line.Append(" [").Append(Get(map, "controllerType")).Append(' ').Append(Get(map, "controllerId"))
                    .Append(" cat ").Append(Get(map, "categoryId")).Append(" layout ").Append(Get(map, "layoutId"))
                    .Append(' ').Append(guid.Length > 8 ? guid.Substring(0, 8) : guid)
                    .Append(Equals(Get(map, "enabled"), true) ? " on" : " off")
                    .Append(' ').Append(Get(map, "elementMapCount")).Append(']');
            }
            return line.ToString();
        }

        static object GetStatic(Type type, string name)
        {
            try
            {
                PropertyInfo property = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return property == null ? null : property.GetValue(null, null);
            }
            catch (Exception e)
            {
                return "<" + e.GetType().Name + ">";
            }
        }
    }
}
