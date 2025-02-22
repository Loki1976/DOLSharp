/*
 * DAWN OF LIGHT - The first free open source DAoC server emulator
 * 
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU General Public License
 * as published by the Free Software Foundation; either version 2
 * of the License, or (at your option) any later version.
 * 
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with this program; if not, write to the Free Software
 * Foundation, Inc., 59 Temple Place - Suite 330, Boston, MA  02111-1307, USA.
 *
 */
using System;
using System.Linq;
using System.Collections;
using System.Reflection;

using DOL.Database;
using DOL.GS.Quests;
using DOL.GS.ServerRules;

using log4net;

namespace DOL.GS.PacketHandler.Client.v168
{
	[PacketHandlerAttribute(PacketHandlerType.TCP, eClientPackets.PlayerRegionChangeRequest, "Player Region Change Request handler.", eClientStatus.PlayerInGame)]
	public class PlayerRegionChangeRequestHandler : IPacketHandler
	{
		/// <summary>
		/// Defines a logger for this class.
		/// </summary>
		private static readonly ILog Log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

		/// <summary>
		/// Holds jump point types
		/// </summary>
		protected readonly Hashtable m_customJumpPointHandlers = new Hashtable();

		public void HandlePacket(GameClient client, GSPacketIn packet)
		{
			var player = client.Player;
			ushort jumpSpotId;

			if (client.Version < GameClient.eClientVersion.Version1126)
			{
				jumpSpotId = packet.ReadShort();
			}
			else
			{
				jumpSpotId = packet.ReadShortLowEndian();
			}

			eRealm targetRealm = player.Realm;

			if (player.CurrentRegion.Expansion == (int)eClientExpansion.TrialsOfAtlantis && player.CurrentZone.Realm != eRealm.None)
			{
				// if we are in TrialsOfAtlantis then base the target jump on the current region realm instead of the players realm
				// this is only used if zone table has the proper realms defined, otherwise it reverts to old behavior - Tolakram
                targetRealm = player.CurrentZone.Realm;
			}

			var filterRealm = DB.Column(nameof(ZonePoint.Realm)).IsEqualTo((byte)targetRealm).Or(DB.Column(nameof(ZonePoint.Realm)).IsEqualTo(0)).Or(DB.Column(nameof(ZonePoint.Realm)).IsNull());
			var zonePoint = DOLDB<ZonePoint>.SelectObject(DB.Column(nameof(ZonePoint.Id)).IsEqualTo(jumpSpotId).And(filterRealm));

			if (zonePoint == null || zonePoint.TargetRegion == 0)
			{
				ChatUtil.SendDebugMessage(client, $"Invalid Jump (ZonePoint table): [{jumpSpotId}]{((zonePoint == null) ? ". Entry missing!" : ". TargetRegion is 0!")}");
				zonePoint = new ZonePoint();
				zonePoint.Id = jumpSpotId;
				string zonePointLocation = $"Region {player.CurrentRegionID} and coordinates ({player.X},{player.Y},{player.Z})";
				Log.Error($"ZonePoint {jumpSpotId} at {zonePointLocation} on client {client.Version} missing. Either ZonePoint missing or RegionChangeRequestHandler needs to be updated.");
			}

			if (client.Account.PrivLevel > 1)
			{
				client.Out.SendMessage($"JumpSpotID = {jumpSpotId}", eChatType.CT_System, eChatLoc.CL_SystemWindow);
				client.Out.SendMessage($"ZonePoint Target: Region = {zonePoint.TargetRegion}, ClassType = \'{zonePoint.ClassType}\'", eChatType.CT_System, eChatLoc.CL_SystemWindow);
			}

			//Dinberg: Fix - some jump points are handled code side, such as instances.
			//As such, region MAY be zero in the database, so this causes an issue.

			if (zonePoint.TargetRegion != 0)
			{
				Region reg = WorldMgr.GetRegion(zonePoint.TargetRegion);
				if (reg != null)
				{
					// check for target region disabled if player is in a standard region
					// otherwise the custom region should handle OnZonePoint for this check
					if (player.CurrentRegion.IsCustom == false && reg.IsDisabled)
					{
						if ((player.Mission is TaskDungeonMission &&
						     (player.Mission as TaskDungeonMission).TaskRegion.Skin == reg.Skin) == false)
						{
							client.Out.SendMessage("This region has been disabled!", eChatType.CT_System, eChatLoc.CL_SystemWindow);
							if (client.Account.PrivLevel == 1)
								return;
						}
					}
				}
			}

			// Allow the region to either deny exit or handle the zonepoint in a custom way
			if (player.CurrentRegion.OnZonePoint(player, zonePoint) == false)
			{
				return;
			}

			//check caps for battleground
			Battleground bg = GameServer.KeepManager.GetBattleground(zonePoint.TargetRegion);
			if (bg != null)
			{
				if (player.Level < bg.MinLevel && player.Level > bg.MaxLevel &&
					player.RealmLevel >= bg.MaxRealmLevel)
					return;
			}

			IJumpPointHandler customHandler = null;
			if (string.IsNullOrEmpty(zonePoint.ClassType) == false)
			{
				customHandler = (IJumpPointHandler)m_customJumpPointHandlers[zonePoint.ClassType];

				// check for db change to update cached handler
				if (customHandler != null && customHandler.GetType().FullName != zonePoint.ClassType)
				{
					customHandler = null;
				}

				if (customHandler == null)
				{
					//Dinberg - Instances need to use a special handler. This is because some instances will result
					//in duplicated zonepoints, such as if Tir Na Nog were to be instanced for a quest.
					string type = player.CurrentRegion.IsInstance
						? "DOL.GS.ServerRules.InstanceDoorJumpPoint"
						: zonePoint.ClassType;
					Type t = ScriptMgr.GetType(type);

					if (t == null)
					{
						Log.ErrorFormat("jump point {0}: class {1} not found!", zonePoint.Id, zonePoint.ClassType);
					}
					else if (!typeof(IJumpPointHandler).IsAssignableFrom(t))
					{
						Log.ErrorFormat("jump point {0}: class {1} must implement IJumpPointHandler interface!", zonePoint.Id,
						                zonePoint.ClassType);
					}
					else
					{
						try
						{
							customHandler = (IJumpPointHandler)Activator.CreateInstance(t);
						}
						catch (Exception e)
						{
							customHandler = null;
							Log.Error(
								string.Format("jump point {0}: error creating a new instance of jump point handler {1}", zonePoint.Id,
								              zonePoint.ClassType), e);
						}
					}
				}

				if (customHandler != null)
				{
					m_customJumpPointHandlers[zonePoint.ClassType] = customHandler;
				}
			}

			new RegionChangeRequestHandler(player, zonePoint, customHandler).Start(1);
		}

		/// <summary>
		/// Handles player region change requests
		/// </summary>
		protected class RegionChangeRequestHandler : RegionAction
		{
			/// <summary>
			/// Checks whether player is allowed to jump
			/// </summary>
			protected readonly IJumpPointHandler m_checkHandler;

			/// <summary>
			/// The target zone point
			/// </summary>
			protected readonly ZonePoint m_zonePoint;

			/// <summary>
			/// Constructs a new RegionChangeRequestHandler
			/// </summary>
			/// <param name="actionSource">The action source</param>
			/// <param name="zonePoint">The target zone point</param>
			/// <param name="checker">The jump point checker instance</param>
			public RegionChangeRequestHandler(GamePlayer actionSource, ZonePoint zonePoint, IJumpPointHandler checkHandler)
				: base(actionSource)
			{
				if (zonePoint == null)
					throw new ArgumentNullException("zonePoint");

				m_zonePoint = zonePoint;
				m_checkHandler = checkHandler;
			}

			/// <summary>
			/// Called on every timer tick
			/// </summary>
			protected override void OnTick()
			{
				var player = (GamePlayer)m_actionSource;

				Region reg = WorldMgr.GetRegion(m_zonePoint.TargetRegion);
				if (reg != null && reg.Expansion > (int)player.Client.ClientType)
				{
					player.Out.SendMessage("Destination region (" + reg.Description + ") is not supported by your client type.",
					                       eChatType.CT_System, eChatLoc.CL_SystemWindow);
					return;
				}

				if (m_checkHandler != null)
				{
					try
					{
						if (!m_checkHandler.IsAllowedToJump(m_zonePoint, player))
							return;
					}
					catch (Exception e)
					{
						if (Log.IsErrorEnabled)
							Log.Error("Jump point handler (" + m_zonePoint.ClassType + ")", e);

						player.Out.SendMessage("exception in jump point (" + m_zonePoint.Id + ") handler...", eChatType.CT_System,
						                       eChatLoc.CL_SystemWindow);
						return;
					}
				}

				//move the player
				player.MoveTo(m_zonePoint.TargetRegion, m_zonePoint.TargetX, m_zonePoint.TargetY, m_zonePoint.TargetZ, m_zonePoint.TargetHeading);
			}
		}
	}
}