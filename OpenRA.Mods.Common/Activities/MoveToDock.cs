#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Activities;
using OpenRA.Mods.Common.Traits;
using OpenRA.Primitives;
using OpenRA.Traits;

namespace OpenRA.Mods.Common.Activities
{
	public class MoveToDock : Activity
	{
		const int MaxBlockedMoves = 3;

		readonly DockClientManager dockClient;
		readonly IDockHost originalDockHost;
		readonly INotifyDockClientMoving[] notifyDockClientMoving;
		readonly Color? dockLineColor;
		readonly MoveCooldownHelper moveCooldownHelper;
		readonly bool forceEnter;
		readonly bool ignoreOccupancy;
		Actor dockHostActor;
		IDockHost dockHost;
		bool dockingCancelled;
		int blockedMoveAttempts;
		IDockHost lastMoveHost;
		IDockHost lastFailedDockHost;

		public MoveToDock(Actor self, Actor dockHostActor = null, IDockHost dockHost = null,
			bool forceEnter = false, bool ignoreOccupancy = false, Color? dockLineColor = null)
		{
			dockClient = self.Trait<DockClientManager>();
			this.dockHostActor = dockHostActor;
			this.dockHost = originalDockHost = dockHost;
			this.forceEnter = forceEnter;
			this.ignoreOccupancy = ignoreOccupancy;
			this.dockLineColor = dockLineColor;
			notifyDockClientMoving = self.TraitsImplementing<INotifyDockClientMoving>().ToArray();
			moveCooldownHelper = new MoveCooldownHelper(self.World, self.Trait<IMove>() as Mobile) { RetryIfDestinationBlocked = true };
		}

		protected override void OnFirstRun(Actor self)
		{
			if (dockClient.IsTraitDisabled)
				return;

			// We were ordered to dock to an actor but host was unspecified.
			if (dockHostActor != null && dockHost == null)
			{
				if (dockHostActor.IsDead || !dockHostActor.IsInWorld)
				{
					dockingCancelled = true;
					return;
				}

				var link = dockClient.AvailableDockHosts(dockHostActor, default, forceEnter, ignoreOccupancy)
					.ClosestDock(self, dockClient);

				if (link.HasValue)
					dockHost = link.Value.Trait;
				else
					dockingCancelled = true;
			}
		}

		public override bool Tick(Actor self)
		{
			if (IsCanceling)
				return true;

			if (dockingCancelled || dockClient.IsTraitDisabled)
			{
				Cancel(self, true);
				return true;
			}

			// Find the nearest DockHost if not explicitly ordered to a specific dock.
			if (dockHost == null || !dockHost.IsEnabledAndInWorld)
			{
				TraitPair<IDockHost>? host;
				if (dockHostActor?.IsDead == false)
				{
					host = dockClient.AvailableDockHosts(dockHostActor, default, forceEnter, ignoreOccupancy)
						.ClosestDock(self, dockClient);
				}
				else
				{
					host = dockClient.ClosestDock(lastFailedDockHost);
				}

				if (host.HasValue)
				{
					dockHost = host.Value.Trait;
					dockHostActor = host.Value.Actor;
				}
				else
				{
					// No docks exist; wait and check again later.
					QueueChild(new Wait(dockClient.Info.SearchForDockDelay));
					return false;
				}
			}

			var result = moveCooldownHelper.Tick(false);
			if (result != null)
				return result.Value;

			if (dockClient.ReserveHost(dockHostActor, dockHost))
			{
				if (dockHost.QueueMoveActivity(this, dockHostActor, self, dockClient, moveCooldownHelper))
				{
					if (lastMoveHost == dockHost)
						blockedMoveAttempts++;
					else
					{
						lastMoveHost = dockHost;
						blockedMoveAttempts = 1;
					}

					if (blockedMoveAttempts > MaxBlockedMoves)
					{
						blockedMoveAttempts = 0;
						dockClient.UnreserveHost();

						// Canceling any child activity is necessary, because at this point QueueMoveActivity has already queued a Move activity,
						// which could succeed by the time MoveToDock.Tick() is called again and due to dockHost reset,
						// a new one is found and thus the client actor can be redirected.
						ChildActivity?.Cancel(self);

						// Try finding another dock host only if consumer/caller of MoveToDock hasn't specified dock host.
						lastFailedDockHost = originalDockHost == null ? dockHost : null;
						dockHost = null;
						lastMoveHost = null;
						return false;
					}

					foreach (var ndcm in notifyDockClientMoving)
						ndcm.MovingToDock(self, dockHostActor, dockHost);

					return false;
				}

				dockHost.QueueDockActivity(this, dockHostActor, self, dockClient);
				return true;
			}
			else
			{
				// Try finding another dock host only if consumer/caller of MoveToDock hasn't specified dock host.
				if (originalDockHost == null)
				{
					var alternativeDock = dockClient.AvailableDockHosts(dockHostActor, default, forceEnter, ignoreOccupancy)
						.ClosestDock(self, dockClient);

					if (alternativeDock.HasValue && alternativeDock.Value.Trait != dockHost)
					{
						dockClient.UnreserveHost();
						dockHost = alternativeDock.Value.Trait;
						dockHostActor = alternativeDock.Value.Actor;
						return false;
					}
				}

				foreach (var ndcm in notifyDockClientMoving)
					ndcm.MovementCancelled(self);

				// The dock explicitly chosen by the user is currently occupied. Wait and check again.
				QueueChild(new Wait(dockClient.Info.SearchForDockDelay));
				return false;
			}
		}

		public override void Cancel(Actor self, bool keepQueue = false)
		{
			dockClient.UnreserveHost();
			foreach (var ndcm in notifyDockClientMoving)
				ndcm.MovementCancelled(self);

			base.Cancel(self, keepQueue);
		}

		public override IEnumerable<TargetLineNode> TargetLineNodes(Actor self)
		{
			if (!dockLineColor.HasValue)
				yield break;

			if (dockHostActor != null)
				yield return new TargetLineNode(Target.FromActor(dockHostActor), dockLineColor.Value);
			else
			{
				if (dockClient.ReservedHostActor != null)
					yield return new TargetLineNode(Target.FromActor(dockClient.ReservedHostActor), dockLineColor.Value);
			}
		}
	}
}
