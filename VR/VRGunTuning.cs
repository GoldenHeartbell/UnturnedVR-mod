using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SDG.Unturned;
using UnityEngine;

namespace UnturnedVR.VR
{
    /// <summary>
    /// Where a gun sits relative to the controller, per gun class and per item id, read from gun-offsets.txt next to
    /// the module DLL (created with defaults when missing). The file is re-read on equip when it changed, so it can
    /// be edited while the game runs: change it, re-equip the gun.
    /// Line format: "<c>name x y z [pitch]</c>", x right / y up / z forward in metres in the controller's frame, pitch
    /// in degrees (positive = muzzle down). Names: pistol (inventory width up to 2), compact (3), rifle (4+), or
    /// "item 363" or "item &lt;guid&gt;" for one gun (modded guns may have a clashing or no numeric id, the GUID is
    /// unique). Item lines override classes. Lines starting with # are comments.
    /// Two more optional numbers follow the pitch: yaw and roll in degrees ("name x y z pitch yaw roll"). Binoculars use the
    /// class name "optic" (or "item &lt;id|guid&gt;"): "optic 0 0.07 0 0" lifts them 7 cm off the hand; the log prints id and guid.
    /// "smooth optic 2 0.1" sets the smoothing of the binoculars' view, which follows the head (minCutoff in Hz: lower = steadier, more lag;
    /// beta: higher = less lag when sweeping); read when the binoculars are raised.
    /// Cost: one file timestamp check per equip.
    /// </summary>
    internal static class VRGunTuning
    {
        internal struct Pose
        {
            public Vector3 Offset;
            public Quaternion Rotation;
            public Vector3 Support;       // second-hand grip point moved by this (gun frame: x right, y up, z forward, metres)
            public float SupportFraction; // without a Grip hook: this fraction of the way from the hand to the muzzle
        }

        private const string FileName = "gun-offsets.txt";

        // Only comments: the built-in defaults live in the code (BuiltIn), so a mod update can improve them and
        // the file only ever holds the player's own overrides. Never write or ship values here.
        private const string DefaultContent =
            "# Your own gun position overrides. Empty = the mod's built-in values. Edit while playing, then re-equip the gun.\n" +
            "# name x y z [pitch]   x = right, y = up, z = forward (metres), pitch in degrees (positive = muzzle down)\n" +
            "# Classes by inventory width: pistol (1-2), compact (3), rifle (4+). \"item <id or guid>\" overrides one gun (the log prints both).\n" +
            "# The log prints each equipped gun with its id and class. Examples (remove the # to use):\n" +
            "# rifle 0 0.09 0 25\n" +
            "# item 363 0 0.1 0.02 20\n" +
            "# Where the second hand holds the gun (two-handed): \"support <class>|item <id|guid> x y z\" moves it from the\n" +
            "# Grip hook / default point (x right, y up, z forward along the barrel, metres). Examples:\n" +
            "# support rifle 0 -0.02 0.03\n" +
            "# support item 363 0 0 -0.05\n";

        // Built-in per-class defaults, used when the file has no line for the class.
        private static Pose BuiltIn(string className)
        {
            // Rifles: the model origin sits near the trigger guard, so push the gun forward (z) to put the hand on the rear grip.
            return className == "rifle" ? Make(new Vector3(0f, 0.09f, 0.07f), 25f) : Make(new Vector3(0f, 0.09f, 0f), 0f);
        }

        private static readonly Dictionary<string, Pose> classes = new Dictionary<string, Pose>();
        private static readonly Dictionary<string, Pose> items = new Dictionary<string, Pose>();
        private static readonly Dictionary<string, Vector3> classSupports = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, Vector3> itemSupports = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, Vector2> smooths = new Dictionary<string, Vector2>(); // x = minCutoff, y = beta (NaN = default)
        private static DateTime loadedStamp = DateTime.MinValue;
        private static string path;

        public static Pose Get(ItemGunAsset gun, out string className)
        {
            Reload();
            className = gun.size_x <= 2 ? "pistol" : gun.size_x == 3 ? "compact" : "rifle";
            string guid = gun.GUID.ToString("N");
            string id = gun.id != 0 ? gun.id.ToString() : null;
            if (!(items.TryGetValue(guid, out Pose pose)
                || (id != null && items.TryGetValue(id, out pose))
                || classes.TryGetValue(className, out pose)))
            {
                pose = BuiltIn(className);
            }

            // Where the second hand holds the gun: the Grip hook, else this fraction of the way to the muzzle,
            // moved by the support offset of the file.
            pose.SupportFraction = className == "pistol" ? 0.4f : className == "compact" ? 0.5f : 0.55f;
            pose.Support = Vector3.zero;
            if (!(itemSupports.TryGetValue(guid, out Vector3 support)
                || (id != null && itemSupports.TryGetValue(id, out support))
                || classSupports.TryGetValue(className, out support)))
            {
                support = Vector3.zero;
            }
            pose.Support = support;
            return pose;
        }

        /// <summary>
        /// Where a pair of binoculars sits relative to the controller: the model is moved off the hand so the hand
        /// holds it from below instead of sticking through the lenses. "optic" is the class line in the file, "item
        /// &lt;id|guid&gt;" overrides one item. Same file, same format; only the offset and rotation are used.
        /// </summary>
        public static Pose GetOptic(ItemOpticAsset optic, out string className)
        {
            Reload();
            className = "optic";
            string guid = optic.GUID.ToString("N");
            string id = optic.id != 0 ? optic.id.ToString() : null;
            if (!(items.TryGetValue(guid, out Pose pose)
                || (id != null && items.TryGetValue(id, out pose))
                || classes.TryGetValue(className, out pose)))
            {
                // The model origin sits in the middle of the binoculars, so lift it by about half its height.
                pose = Make(new Vector3(0f, 0.07f, 0f), 0f);
            }
            return pose;
        }

        /// <summary>
        /// Hand-shake filter numbers of one aiming feature ("optic", later "gun"): the file's "smooth &lt;name&gt; &lt;minCutoff&gt;
        /// [beta]" line, else the given defaults. Read when the feature starts (a file check), not every frame.
        /// </summary>
        public static void GetSmoothing(string name, float defaultMinCutoff, float defaultBeta, out float minCutoff, out float beta)
        {
            Reload();
            minCutoff = defaultMinCutoff;
            beta = defaultBeta;
            if (smooths.TryGetValue(name, out Vector2 value))
            {
                minCutoff = value.x;
                if (!float.IsNaN(value.y))
                {
                    beta = value.y;
                }
            }
        }

        private static Pose Make(Vector3 offset, float pitch, float yaw = 0f, float roll = 0f)
        {
            return new Pose { Offset = offset, Rotation = Quaternion.Euler(pitch, yaw, roll) };
        }

        private static void Reload()
        {
            try
            {
                if (path == null)
                {
                    path = Path.Combine(Path.GetDirectoryName(typeof(VRGunTuning).Assembly.Location), FileName);
                }
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, DefaultContent);
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (stamp == loadedStamp)
                {
                    return;
                }
                loadedStamp = stamp;
                classes.Clear();
                items.Clear();
                classSupports.Clear();
                itemSupports.Clear();
                smooths.Clear();
                foreach (string raw in File.ReadAllLines(path))
                {
                    ParseLine(raw);
                }
                VRLog.Info("gun: read " + FileName + " (" + classes.Count + " classes, " + items.Count + " items).");
            }
            catch (Exception exception)
            {
                VRLog.Warn("gun: cannot read " + FileName + ": " + exception.Message);
            }
        }

        private static void ParseLine(string raw)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                return;
            }
            string[] parts = line.Split((char[]) null, StringSplitOptions.RemoveEmptyEntries);
            if (parts[0].Equals("smooth", StringComparison.OrdinalIgnoreCase))
            {
                // "smooth <name> <minCutoff> [beta]": hand-shake filter (VRAimFilter) of one aiming feature, e.g. "optic".
                if (parts.Length >= 3 && TryFloat(parts[2], out float minCutoff))
                {
                    float beta = float.NaN;
                    if (parts.Length >= 4 && TryFloat(parts[3], out float parsedBeta))
                    {
                        beta = parsedBeta;
                    }
                    smooths[parts[1].ToLowerInvariant()] = new Vector2(minCutoff, beta);
                }
                else
                {
                    VRLog.Warn("gun: ignored line in " + FileName + ": " + line);
                }
                return;
            }
            bool isSupport = parts[0].Equals("support", StringComparison.OrdinalIgnoreCase);
            if (isSupport)
            {
                // "support <class>|item <id|guid> x y z": moves the second hand's grip point (no pitch).
                string[] rest = new string[parts.Length - 1];
                Array.Copy(parts, 1, rest, 0, rest.Length);
                parts = rest;
                if (parts.Length == 0)
                {
                    return;
                }
            }
            bool isItem = parts[0].Equals("item", StringComparison.OrdinalIgnoreCase);
            int first = isItem ? 2 : 1; // index of x
            if (parts.Length < first + 3 || !TryFloat(parts[first], out float x) || !TryFloat(parts[first + 1], out float y)
                || !TryFloat(parts[first + 2], out float z))
            {
                VRLog.Warn("gun: ignored line in " + FileName + ": " + line);
                return;
            }
            float pitch = 0f;
            float yaw = 0f;
            float roll = 0f;
            if (parts.Length > first + 3)
            {
                TryFloat(parts[first + 3], out pitch);
            }
            if (parts.Length > first + 4)
            {
                TryFloat(parts[first + 4], out yaw); // optional, mostly for binoculars: turn about the up axis
            }
            if (parts.Length > first + 5)
            {
                TryFloat(parts[first + 5], out roll); // optional: tilt about the forward axis
            }

            if (isSupport)
            {
                Vector3 move = new Vector3(x, y, z);
                if (isItem)
                {
                    itemSupports[Guid.TryParse(parts[1], out Guid supportGuid) ? supportGuid.ToString("N") : parts[1]] = move;
                }
                else
                {
                    classSupports[parts[0].ToLowerInvariant()] = move;
                }
                return;
            }

            Pose pose = Make(new Vector3(x, y, z), pitch, yaw, roll);
            if (isItem)
            {
                // A numeric id or a GUID (any common spelling, stored as 32 hex digits).
                items[Guid.TryParse(parts[1], out Guid guid) ? guid.ToString("N") : parts[1]] = pose;
                return;
            }
            classes[parts[0].ToLowerInvariant()] = pose;
        }

        private static bool TryFloat(string text, out float value)
        {
            return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
