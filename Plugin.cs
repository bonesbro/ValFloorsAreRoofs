using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Jotunn;

namespace FloorsAreRoofs
{
	[BepInPlugin(PluginId, FloorsAreRoofsPlugin.ModName, FloorsAreRoofsPlugin.Version)]
	[BepInDependency(Jotunn.Main.ModGuid)]
	public class FloorsAreRoofsPlugin : BaseUnityPlugin
	{
		private const string PluginId = "bonesbro.val.floorsareroofs";
		public const string Version = "2.0.1";
		public const string ModName = "Floors Are Roofs";
		private const string DefaultPrefabsConfigSetting = "wood_floor,wood_floor_1x1";
		private const string DefaultHammersConfigSetting = "Hammer";
		private const bool DefaultRainDamageConfigSetting = true;

		protected static bool PatchingHasAlreadySucceeded = false;
		internal static bool RemoveRainDamage = true;
		internal static string PrefabListInConfigSettings = "";
		internal static string HammersListInConfigSettings = "";
		internal static string[] PrefabList = new string[0];
		internal static string[] HammerList = new string[0];

		Harmony _Harmony;
		public static ManualLogSource Log;

		private void Awake()
		{
#if DEBUG
			Log = Logger;
#else
			Log = new ManualLogSource(null);
#endif
			_Harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), null);
			Config.Bind<int>("General", "NexusID", 1039, "Nexus mod ID for updates");
			PrefabListInConfigSettings = Config.Bind<string>("General", "PrefabsToChange", DefaultPrefabsConfigSetting, $"List of the prefab pieces to weatherproof.  Comma-separate each prefab name.  Default: {DefaultPrefabsConfigSetting}").Value;
			HammersListInConfigSettings = Config.Bind<string>("General", "Hammers", DefaultHammersConfigSetting, $"List of hammer tools whose recipes we'll search to find the pieces to update.  These are the prefab names for each hammer piece.  Comma-separate each prefab name.  Default: {DefaultHammersConfigSetting}").Value;
			RemoveRainDamage = Config.Bind<bool>("General", "RemoveRainDamage", DefaultRainDamageConfigSetting, "Prevent the floor pieces from taking wear and tear damage from rain.").Value;

			Debug.Log($"[floorsareroofs]: Initializing using config settings: PrefabsToChange:[{PrefabListInConfigSettings ?? "null"}], Hammers:[{HammersListInConfigSettings ?? "null"}], RemoveRainDamage:[{RemoveRainDamage}]");

			// When a config setting isn't present, returns null for the config setting, though it will write out the default setting when you save and quit.
			// To ensure the mod has the correct settings on first launch, manually assign the defaults here
			if (string.IsNullOrWhiteSpace(PrefabListInConfigSettings))
				PrefabListInConfigSettings = DefaultPrefabsConfigSetting;
			if (string.IsNullOrWhiteSpace(HammersListInConfigSettings))
				HammersListInConfigSettings = DefaultHammersConfigSetting;

			// Normalize them
			string prefabsNormalized = PrefabListInConfigSettings.Replace(" ", "");
			string hammersNormalized = HammersListInConfigSettings.Replace(" ", "");

			char[] seperators = { ',' };
			PrefabList = prefabsNormalized.Split(seperators, System.StringSplitOptions.RemoveEmptyEntries);
			HammerList = hammersNormalized.Split(seperators, System.StringSplitOptions.RemoveEmptyEntries);

			// Ask the Jotunn library to call us once all custom pieces have been added.
			Jotunn.Managers.PieceManager.OnPiecesRegistered += PieceManager_OnPiecesRegistered;
		}

		private void PieceManager_OnPiecesRegistered()
		{
			Debug.Log("[floorsareroofs]: Triggered from PieceManager_OnPiecesRegistered");
			PatchFloor();
		}

		private void OnDestroy()
		{
			_Harmony?.UnpatchAll(PluginId);
		}

		internal static void PatchFloor()
		{
			Debug.Log("[floorsareroofs]: Beginning patch attempt");

			// Sanity check the config settings
			if (PrefabList.Length == 0)
			{
				Debug.LogError("[floorsareroofs]: Error: No prefabs defined in config setting PrefabsToChange.");
				return;
			}
			if (HammerList.Length == 0)
			{
				Debug.LogError("[floorsareroofs]: Error: No prefabs defined in config setting Hammers.");
				return;
			}

			// When we first wake up the ObjectDB hasn't been instantiated yet
			if (ObjectDB.instance == null)
			{
				Debug.LogError("[floorsareroofs]: ObjectDB is null");
				return;
			}

			// This is where we do all of the real work.
			int cTotalFloorsUpdated = 0;
			bool completeSuccess = true;
			foreach (string hammerName in HammerList)
			{
				int cFloorsUpdated = 0;
				completeSuccess &= UpdatePiecesInHammer(hammerName, out cFloorsUpdated);
				cTotalFloorsUpdated += cFloorsUpdated;
			}

			Debug.Log($"[floorsareroofs]: Successfully updated {cTotalFloorsUpdated} floors.  Config setting has {PrefabList.Length} floors specified.");

			if (completeSuccess)
			{
				if (cTotalFloorsUpdated == PrefabList.Length)
				{
					Debug.Log("[floorsareroofs]: Floors updated finished successfully.");
				}
				else
				{
					Debug.Log($"[floorsareroofs]: Floor update finished, but it looks like we couldn't find some of the floors you specified.  Please doublecheck your config settings.");
					LogConfigSettingHelp();
				}
			}
			else
			{
				// Be helpful!
				Debug.LogError("[floorsareroofs]: Floor update finished, but there were errors.  Please doublecheck your config settings.");
				LogConfigSettingHelp();
			}
		}

		private static void LogConfigSettingHelp()
		{
			Debug.Log($"[floorsareroofs]: The config settings are case-sensitive, each item should be separated by a comma, and there should be no spaces before or after the commas.");

			if (HammersListInConfigSettings != DefaultHammersConfigSetting)
				Debug.Log($"[floorsareroofs]: You have a non-default config setting for Hammers.  Default: {DefaultHammersConfigSetting}.  Your setting: {HammersListInConfigSettings}");
			if (PrefabListInConfigSettings != DefaultPrefabsConfigSetting)
				Debug.Log($"[floorsareroofs]: You have a non-default config setting for PrefabsToChange.  Default: {DefaultPrefabsConfigSetting}.  Your setting: {PrefabListInConfigSettings}");
		}


		/// <summary>Looks through the specified hammer to update all of the specified floors referenced by the hammer</summary>
		/// <param name="hammerName">The prefab name of the hammer to modify</param>
		/// <param name="cFloorsUpdated">Out: The count of floors we successfuly found and updated in this hammer</param>
		/// <returns>True if we were able to update floors, or false if we failed</returns>
		private static bool UpdatePiecesInHammer(string hammerName, out int cFloorsUpdated)
		{
			cFloorsUpdated = 0;

			Debug.Log($"[floorsareroofs]: Starting to patches pieces in Hammer {hammerName}.");

			GameObject hammer = ObjectDB.instance.GetItemPrefab(hammerName);
			if (hammer == null)
			{
				Debug.LogError($"[floorsareroofs]: Could not find tool {hammerName} in ObjectDB.");
				return false;
			}

			ItemDrop hammerItemDrop;
			if (!hammer.TryGetComponent<ItemDrop>(out hammerItemDrop))
			{
				Debug.LogError($"[floorsareroofs]: Could not get itemdrop from hammer {hammerName}");
				return false;
			}

			PieceTable hammerPieceTable = hammerItemDrop?.m_itemData?.m_shared?.m_buildPieces;
			if (hammerPieceTable == null)
			{
				Debug.LogError($"[floorsareroofs]: Could not find piecetable in hammer {hammerName}");
				return false;
			}

			if (hammerPieceTable.m_pieces == null || hammerPieceTable.m_pieces.Count == 0)
			{
				Debug.LogError($"[floorsareroofs]: Could not find any pieces in hammerPieceTable for hammer {hammerName}");
				return false;
			}

			//List<GameObject> floors = hammerPieceTable.m_pieces.FindAll(i => i?.name == "wood_floor" || i?.name == "wood_floor_1x1");
			List<GameObject> floors = hammerPieceTable.m_pieces.FindAll(i => PrefabList.Contains(i?.name));
			if (floors == null || floors.Count == 0)
			{
				Debug.Log($"[floorsareroofs]: Could not find any of the specified floors in hammer piece {hammerName}.  Continuing...");
				return true;
			}

			foreach (GameObject go in floors)
			{
				bool successful = PatchAFloor(go);
				if (successful)
					cFloorsUpdated++;
			}

			Debug.Log($"[floorsareroofs]: Finished with hammer {hammerName}.  Sucessfully updated {cFloorsUpdated} floors.");

			return true;
		}

		/// <summary>Updates a particular floor to make it act like a roof, and optionally prevent it from taking rain damage</summary>
		/// <param name="go">The GameObject for the floor we'll modify</param>
		/// <returns>True if we succeeded, or false if we failed</returns>
		private static bool PatchAFloor(GameObject go)
		{
			Debug.Log($"[floorsareroofs]: Preparing to patch {go.name}");

			Transform tr;
			if (!go.TryGetComponent<Transform>(out tr))
			{
				Debug.LogError($"[floorsareroofs]: Could not find Transform in floor {go.name}");
				return false;
			}

			WearNTear wn;
			if (!go.TryGetComponent<WearNTear>(out wn))
			{
				Debug.LogError($"[floorsareroofs]: Could not find WearNTear in floor {go.name}");
				return false;
			}

			bool foundCollider = false;
			for (int iChild = 0; iChild < tr.childCount; iChild++)
			{
				Transform trChild = tr.GetChild(iChild);

				if (trChild?.name != "collider")
					continue;

				foundCollider = true;
				if (trChild.tag == "leaky")
				{
					trChild.tag = "roof";
					Debug.Log($"[floorsareroofs] Successfully patched {go.name} into a roof");
				}
				else if (trChild.tag == "roof")
				{
					Debug.Log($"[floorsareroofs] {go.name} is already a roof");
				}
				else
				{
					Debug.LogError($"[floorsareroofs]: Could not patch {go.name}.  Its collider has tag '{trChild.tag ?? "[null]"}' instead of 'leaky'");
				}
			}

			if (!foundCollider)
			{
				Debug.LogError($"[floorsareroofs]: Could not find collider in {go.name}");
				return false;
			}

			if (RemoveRainDamage)
			{
				if (wn.m_noRoofWear)
				{
					Debug.Log($"[floorsareroofs] {go.name} patched to not degrade in the rain");
					wn.m_noRoofWear = false;
				}
				else
				{
					Debug.Log($"[floorsareroofs] {go.name} already does not degrade in the rain");
				}
			}
			else
			{
				Debug.Log($"[floorsareroofs] Not updating {go.name}.m_noRoofWear because config setting RemoveRainDamage is false");
			}

			return true;
		}
	}
}
