﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Localization;
using HtmlAgilityPack;
using Newtonsoft.Json;
using SteamKit2;

namespace ArchiSteamFarm {
	internal sealed class CardsFarmer : IDisposable {
		private const byte HoursToBump = 2; // How many hours are required for restricted accounts
		private const byte HoursToIgnore = 24; // How many hours we ignore unreleased appIDs and don't bother checking them again

		private static readonly ConcurrentDictionary<uint, DateTime> IgnoredAppIDs = new ConcurrentDictionary<uint, DateTime>(); // Reserved for unreleased games
		private static readonly HashSet<uint> UntrustedAppIDs = new HashSet<uint> { 440, 570, 730 };

		[JsonProperty]
		internal readonly ConcurrentHashSet<Game> CurrentGamesFarming = new ConcurrentHashSet<Game>();

		[JsonProperty]
		internal readonly ConcurrentSortedHashSet<Game> GamesToFarm = new ConcurrentSortedHashSet<Game>();

		[JsonProperty]
		internal TimeSpan TimeRemaining => new TimeSpan(
			Bot.BotConfig.CardDropsRestricted ? (int) Math.Ceiling(GamesToFarm.Count / (float) ArchiHandler.MaxGamesPlayedConcurrently) * HoursToBump : 0,
			30 * GamesToFarm.Sum(game => game.CardsRemaining),
			0
		);

		private readonly Bot Bot;
		private readonly SemaphoreSlim EventSemaphore = new SemaphoreSlim(1);
		private readonly SemaphoreSlim FarmingSemaphore = new SemaphoreSlim(1);
		private readonly ManualResetEventSlim FarmResetEvent = new ManualResetEventSlim(false);
		private readonly Timer IdleFarmingTimer;

		[JsonProperty]
		internal bool Paused { get; private set; }

		private bool KeepFarming;
		private bool NowFarming;
		private bool ParsingScheduled;
		private bool StickyPause;

		internal CardsFarmer(Bot bot) {
			Bot = bot ?? throw new ArgumentNullException(nameof(bot));

			if (Program.GlobalConfig.IdleFarmingPeriod > 0) {
				IdleFarmingTimer = new Timer(
					async e => await CheckGamesForFarming().ConfigureAwait(false),
					null,
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod) + TimeSpan.FromSeconds(Program.LoadBalancingDelay * Bot.Bots.Count), // Delay
					TimeSpan.FromHours(Program.GlobalConfig.IdleFarmingPeriod) // Period
				);
			}
		}

		public void Dispose() {
			// Those are objects that are always being created if constructor doesn't throw exception
			EventSemaphore.Dispose();
			FarmingSemaphore.Dispose();
			FarmResetEvent.Dispose();

			// Those are objects that might be null and the check should be in-place
			IdleFarmingTimer?.Dispose();
		}

		internal void OnDisconnected() {
			if (!NowFarming) {
				return;
			}

			StopFarming().Forget();
		}

		internal async Task OnNewGameAdded() {
			// We aim to have a maximum of 2 tasks, one already parsing, and one waiting in the queue
			// This way we can call this function as many times as needed e.g. because of Steam events
			lock (EventSemaphore) {
				if (ParsingScheduled) {
					return;
				}

				ParsingScheduled = true;
			}

			await EventSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				lock (EventSemaphore) {
					ParsingScheduled = false;
				}

				// If we're not farming yet, obviously it's worth it to make a check
				if (!NowFarming) {
					await StartFarming().ConfigureAwait(false);
					return;
				}

				// If we have Complex algorithm and some games to boost, it's also worth to make a re-check, but only in this case
				// That's because we would check for new games after our current round anyway, and having extra games in the queue right away doesn't change anything
				// Therefore, there is no need for extra restart of CardsFarmer if we have no games under HoursToBump hours in current round
				if (Bot.BotConfig.CardDropsRestricted && (GamesToFarm.Count > 0) && (GamesToFarm.Min(game => game.HoursPlayed) < HoursToBump)) {
					await StopFarming().ConfigureAwait(false);
					await StartFarming().ConfigureAwait(false);
				}
			} finally {
				EventSemaphore.Release();
			}
		}

		internal async Task OnNewItemsNotification() {
			if (NowFarming) {
				FarmResetEvent.Set();
				return;
			}

			// If we're not farming, and we got new items, it's likely to be a booster pack or likewise
			// In this case, perform a loot if user wants to do so
			await Bot.LootIfNeeded().ConfigureAwait(false);
		}

		internal async Task Pause(bool sticky) {
			if (sticky) {
				StickyPause = true;
			}

			Paused = true;
			if (NowFarming) {
				await StopFarming().ConfigureAwait(false);
			}
		}

		internal async Task Resume(bool userAction) {
			if (StickyPause) {
				if (!userAction) {
					Bot.ArchiLogger.LogGenericInfo(Strings.IgnoredStickyPauseEnabled);
					return;
				}

				StickyPause = false;
			}

			Paused = false;
			if (!NowFarming) {
				await StartFarming().ConfigureAwait(false);
			}
		}

		internal void SetInitialState(bool paused) => StickyPause = Paused = paused;

		internal async Task StartFarming() {
			if (NowFarming || Paused || !Bot.IsPlayingPossible) {
				return;
			}

			if (!Bot.CanReceiveSteamCards) {
				await Bot.OnFarmingFinished(false).ConfigureAwait(false);
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (NowFarming || Paused || !Bot.IsPlayingPossible) {
					return;
				}

				bool? isAnythingToFarm = await IsAnythingToFarm().ConfigureAwait(false);
				if (isAnythingToFarm == null) {
					return;
				}

				if (!isAnythingToFarm.Value) {
					Bot.ArchiLogger.LogGenericInfo(Strings.NothingToIdle);
					await Bot.OnFarmingFinished(false).ConfigureAwait(false);
					return;
				}

				if (GamesToFarm.Count == 0) {
					Bot.ArchiLogger.LogNullError(nameof(GamesToFarm));
					return;
				}

				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.GamesToIdle, GamesToFarm.Count, GamesToFarm.Sum(game => game.CardsRemaining), TimeRemaining.ToHumanReadable()));

				// This is the last moment for final check if we can farm
				if (!Bot.IsPlayingPossible) {
					Bot.ArchiLogger.LogGenericInfo(Strings.PlayingNotAvailable);
					return;
				}

				if (Bot.PlayingWasBlocked) {
					await Task.Delay(Bot.MinPlayingBlockedTTL * 1000).ConfigureAwait(false);

					if (!Bot.IsPlayingPossible) {
						Bot.ArchiLogger.LogGenericInfo(Strings.PlayingNotAvailable);
						return;
					}
				}

				KeepFarming = NowFarming = true;
				Utilities.StartBackgroundFunction(Farm);
			} finally {
				FarmingSemaphore.Release();
			}
		}

		internal async Task StopFarming() {
			if (!NowFarming) {
				return;
			}

			await FarmingSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				if (!NowFarming) {
					return;
				}

				KeepFarming = false;
				FarmResetEvent.Set();

				for (byte i = 0; (i < 5) && NowFarming; i++) {
					await Task.Delay(1000).ConfigureAwait(false);
				}

				if (NowFarming) {
					NowFarming = false;
				}

				Bot.ArchiLogger.LogGenericInfo(Strings.IdlingStopped);
				Bot.OnFarmingStopped();
			} finally {
				FarmingSemaphore.Release();
			}
		}

		private async Task CheckGame(uint appID, string name, float hours, byte badgeLevel) {
			if ((appID == 0) || string.IsNullOrEmpty(name) || (hours < 0)) {
				Bot.ArchiLogger.LogNullError(nameof(appID) + " || " + nameof(name) + " || " + nameof(hours));
				return;
			}

			ushort? cardsRemaining = await GetCardsRemaining(appID).ConfigureAwait(false);
			if (!cardsRemaining.HasValue) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningCouldNotCheckCardsStatus, appID, name));
				return;
			}

			if (cardsRemaining.Value == 0) {
				return;
			}

			GamesToFarm.Add(new Game(appID, name, hours, cardsRemaining.Value, badgeLevel));
		}

		private async Task CheckGamesForFarming() {
			if (NowFarming || Paused || !Bot.IsConnectedAndLoggedOn) {
				return;
			}

			await StartFarming().ConfigureAwait(false);
		}

		private async Task CheckPage(HtmlDocument htmlDocument) {
			if (htmlDocument == null) {
				Bot.ArchiLogger.LogNullError(nameof(htmlDocument));
				return;
			}

			HtmlNodeCollection htmlNodes = htmlDocument.DocumentNode.SelectNodes("//div[@class='badge_row_inner']");
			if (htmlNodes == null) {
				// No eligible badges whatsoever
				return;
			}

			HashSet<Task> backgroundTasks = new HashSet<Task>();

			foreach (HtmlNode htmlNode in htmlNodes) {
				HtmlNode statsNode = htmlNode.SelectSingleNode(".//div[@class='badge_title_stats_content']");

				HtmlNode appIDNode = statsNode?.SelectSingleNode(".//div[@class='card_drop_info_dialog']");
				if (appIDNode == null) {
					// It's just a badge, nothing more
					continue;
				}

				string appIDString = appIDNode.GetAttributeValue("id", null);
				if (string.IsNullOrEmpty(appIDString)) {
					Bot.ArchiLogger.LogNullError(nameof(appIDString));
					continue;
				}

				string[] appIDSplitted = appIDString.Split('_');
				if (appIDSplitted.Length < 5) {
					Bot.ArchiLogger.LogNullError(nameof(appIDSplitted));
					continue;
				}

				appIDString = appIDSplitted[4];

				if (!uint.TryParse(appIDString, out uint appID) || (appID == 0)) {
					Bot.ArchiLogger.LogNullError(nameof(appID));
					continue;
				}

				if (GlobalConfig.GlobalBlacklist.Contains(appID) || Program.GlobalConfig.Blacklist.Contains(appID)) {
					// We have this appID blacklisted, so skip it
					continue;
				}

				if (IgnoredAppIDs.TryGetValue(appID, out DateTime ignoredUntil)) {
					if (ignoredUntil < DateTime.UtcNow) {
						// This game served its time as being ignored
						IgnoredAppIDs.TryRemove(appID, out _);
					} else {
						// This game is still ignored
						continue;
					}
				}

				// Cards
				HtmlNode progressNode = statsNode.SelectSingleNode(".//span[@class='progress_info_bold']");
				if (progressNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(progressNode));
					continue;
				}

				string progressText = progressNode.InnerText;
				if (string.IsNullOrEmpty(progressText)) {
					Bot.ArchiLogger.LogNullError(nameof(progressText));
					continue;
				}

				ushort cardsRemaining = 0;
				Match progressMatch = Regex.Match(progressText, @"\d+");

				// This might fail if we have no card drops remaining, 0 is not printed in this case - that's fine
				if (progressMatch.Success) {
					if (!ushort.TryParse(progressMatch.Value, out cardsRemaining) || (cardsRemaining == 0)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsRemaining));
						continue;
					}
				}

				if (cardsRemaining == 0) {
					// Normally we'd trust this information and simply skip the rest
					// However, Steam is so fucked up that we can't simply assume that it's correct
					// It's entirely possible that actual game page has different info, and badge page lied to us
					// We can't check every single game though, as this will literally kill people with cards from games they don't own
					if (!UntrustedAppIDs.Contains(appID)) {
						continue;
					}

					// To save us on extra work, check cards earned so far first
					HtmlNode cardsEarnedNode = statsNode.SelectSingleNode(".//div[@class='card_drop_info_header']");
					if (cardsEarnedNode == null) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarnedNode));
						continue;
					}

					string cardsEarnedText = cardsEarnedNode.InnerText;
					if (string.IsNullOrEmpty(cardsEarnedText)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarnedText));
						continue;
					}

					Match cardsEarnedMatch = Regex.Match(cardsEarnedText, @"\d+");
					if (!cardsEarnedMatch.Success) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarnedMatch));
						continue;
					}

					if (!ushort.TryParse(cardsEarnedMatch.Value, out ushort cardsEarned)) {
						Bot.ArchiLogger.LogNullError(nameof(cardsEarned));
						continue;
					}

					if (cardsEarned > 0) {
						// If we already earned some cards for this game, it's very likely that it's done
						// Let's hope that trusting cardsRemaining AND cardsEarned is enough
						// If I ever hear that it's not, I'll most likely need a doctor
						continue;
					}

					// If we have no cardsRemaining and no cardsEarned, it's either:
					// - A game we don't own physically, but we have cards from it in inventory
					// - F2P game that we didn't spend any money in, but we have cards from it in inventory
					// - Steam fuckup
					// As you can guess, we must follow the rest of the logic in case of Steam fuckup
					// Please kill me ;_;
				}

				// Hours
				HtmlNode timeNode = statsNode.SelectSingleNode(".//div[@class='badge_title_stats_playtime']");
				if (timeNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(timeNode));
					continue;
				}

				string hoursString = timeNode.InnerText;
				if (string.IsNullOrEmpty(hoursString)) {
					Bot.ArchiLogger.LogNullError(nameof(hoursString));
					continue;
				}

				float hours = 0.0F;
				Match hoursMatch = Regex.Match(hoursString, @"[0-9\.,]+");

				// This might fail if we have exactly 0.0 hours played, as it's not printed in that case - that's fine
				if (hoursMatch.Success) {
					if (!float.TryParse(hoursMatch.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out hours) || (hours <= 0.0F)) {
						Bot.ArchiLogger.LogNullError(nameof(hours));
						continue;
					}
				}

				// Names
				HtmlNode nameNode = statsNode.SelectSingleNode("(.//div[@class='card_drop_info_body'])[last()]");
				if (nameNode == null) {
					Bot.ArchiLogger.LogNullError(nameof(nameNode));
					continue;
				}

				string name = nameNode.InnerText;
				if (string.IsNullOrEmpty(name)) {
					Bot.ArchiLogger.LogNullError(nameof(name));
					continue;
				}

				// We handle two cases here - normal one, and no card drops remaining
				int nameStartIndex = name.IndexOf(" by playing ", StringComparison.Ordinal);
				if (nameStartIndex <= 0) {
					nameStartIndex = name.IndexOf("You don't have any more drops remaining for ", StringComparison.Ordinal);
					if (nameStartIndex <= 0) {
						Bot.ArchiLogger.LogNullError(nameof(nameStartIndex));
						continue;
					}

					nameStartIndex += 32; // + 12 below
				}

				nameStartIndex += 12;

				int nameEndIndex = name.LastIndexOf('.');
				if (nameEndIndex <= nameStartIndex) {
					Bot.ArchiLogger.LogNullError(nameof(nameEndIndex));
					continue;
				}

				name = WebUtility.HtmlDecode(name.Substring(nameStartIndex, nameEndIndex - nameStartIndex));

				// Levels
				byte badgeLevel = 0;

				HtmlNode levelNode = htmlNode.SelectSingleNode(".//div[@class='badge_info_description']/div[2]");
				if (levelNode != null) {
					// There is no levelNode if we didn't craft that badge yet (level 0)
					string levelString = levelNode.InnerText;
					if (string.IsNullOrEmpty(levelString)) {
						Bot.ArchiLogger.LogNullError(nameof(levelString));
						continue;
					}

					int levelIndex = levelString.IndexOf("Level ", StringComparison.OrdinalIgnoreCase);
					if (levelIndex < 0) {
						Bot.ArchiLogger.LogNullError(nameof(levelIndex));
						continue;
					}

					levelIndex += 6;
					if (levelString.Length <= levelIndex) {
						Bot.ArchiLogger.LogNullError(nameof(levelIndex));
						continue;
					}

					levelString = levelString.Substring(levelIndex, 1);
					if (!byte.TryParse(levelString, out badgeLevel) || (badgeLevel == 0) || (badgeLevel > 5)) {
						Bot.ArchiLogger.LogNullError(nameof(badgeLevel));
						continue;
					}
				}

				// Done with parsing, we have two possible cases here
				// Either we have decent info about appID, name, hours, cardsRemaining (cardsRemaining > 0) and level
				// OR we strongly believe that Steam lied to us, in this case we will need to check game invidually (cardsRemaining == 0)
				if (cardsRemaining > 0) {
					GamesToFarm.Add(new Game(appID, name, hours, cardsRemaining, badgeLevel));
				} else {
					Task task = CheckGame(appID, name, hours, badgeLevel);
					switch (Program.GlobalConfig.OptimizationMode) {
						case GlobalConfig.EOptimizationMode.MinMemoryUsage:
							await task.ConfigureAwait(false);
							break;
						default:
							backgroundTasks.Add(task);
							break;
					}
				}
			}

			// If we have any background tasks, wait for them
			if (backgroundTasks.Count > 0) {
				await Task.WhenAll(backgroundTasks).ConfigureAwait(false);
			}
		}

		private async Task CheckPage(byte page) {
			if (page == 0) {
				Bot.ArchiLogger.LogNullError(nameof(page));
				return;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(page).ConfigureAwait(false);
			if (htmlDocument == null) {
				return;
			}

			await CheckPage(htmlDocument).ConfigureAwait(false);
		}

		private async Task Farm() {
			do {
				// Now the algorithm used for farming depends on whether account is restricted or not
				if (Bot.BotConfig.CardDropsRestricted) {
					// If we have restricted card drops, we use complex algorithm
					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ChosenFarmingAlgorithm, "Complex"));
					while (GamesToFarm.Count > 0) {
						HashSet<Game> playableGamesToFarmSolo = new HashSet<Game>();
						foreach (Game game in GamesToFarm.Where(game => game.HoursPlayed >= HoursToBump)) {
							if (await IsPlayableGame(game).ConfigureAwait(false)) {
								playableGamesToFarmSolo.Add(game);
							}
						}

						if (playableGamesToFarmSolo.Count > 0) {
							while (playableGamesToFarmSolo.Count > 0) {
								Game playableGame = playableGamesToFarmSolo.First();
								if (await FarmSolo(playableGame).ConfigureAwait(false)) {
									playableGamesToFarmSolo.Remove(playableGame);
								} else {
									NowFarming = false;
									return;
								}
							}
						} else {
							HashSet<Game> playableGamesToFarmMultiple = new HashSet<Game>();
							foreach (Game game in GamesToFarm.Where(game => game.HoursPlayed < HoursToBump).OrderByDescending(game => game.HoursPlayed)) {
								if (await IsPlayableGame(game).ConfigureAwait(false)) {
									playableGamesToFarmMultiple.Add(game);
								}

								if (playableGamesToFarmMultiple.Count >= ArchiHandler.MaxGamesPlayedConcurrently) {
									break;
								}
							}

							if (FarmMultiple(playableGamesToFarmMultiple)) {
								Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.IdlingFinishedForGames, string.Join(", ", playableGamesToFarmMultiple.Select(game => game.AppID))));
							} else {
								NowFarming = false;
								return;
							}
						}
					}
				} else {
					// If we have unrestricted card drops, we use simple algorithm
					Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.ChosenFarmingAlgorithm, "Simple"));
					while (GamesToFarm.Count > 0) {
						Game playableGame = null;
						foreach (Game game in GamesToFarm) {
							if (!await IsPlayableGame(game).ConfigureAwait(false)) {
								continue;
							}

							playableGame = game;
							break;
						}

						if (playableGame != null) {
							if (await FarmSolo(playableGame).ConfigureAwait(false)) {
								continue;
							}
						}

						NowFarming = false;
						return;
					}
				}
			} while ((await IsAnythingToFarm().ConfigureAwait(false)).GetValueOrDefault());

			CurrentGamesFarming.Clear();
			NowFarming = false;

			Bot.ArchiLogger.LogGenericInfo(Strings.IdlingFinished);
			await Bot.OnFarmingFinished(true).ConfigureAwait(false);
		}

		private async Task<bool> Farm(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return false;
			}

			bool success = true;

			if (game.AppID != game.PlayableAppID) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningIdlingGameMismatch, game.AppID, game.GameName, game.PlayableAppID));
			}

			Bot.IdleGame(game.PlayableAppID);
			DateTime endFarmingDate = DateTime.UtcNow.AddHours(Program.GlobalConfig.MaxFarmingTime);

			bool? keepFarming = await ShouldFarm(game).ConfigureAwait(false);
			while (keepFarming.GetValueOrDefault(true) && (DateTime.UtcNow < endFarmingDate)) {
				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.StillIdling, game.AppID, game.GameName));

				DateTime startFarmingPeriod = DateTime.UtcNow;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				game.HoursPlayed += (float) DateTime.UtcNow.Subtract(startFarmingPeriod).TotalHours;

				if (!success) {
					break;
				}

				keepFarming = await ShouldFarm(game).ConfigureAwait(false);
			}

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.StoppedIdling, game.AppID, game.GameName));
			return success;
		}

		private bool FarmHours(ConcurrentHashSet<Game> games) {
			if ((games == null) || (games.Count == 0)) {
				Bot.ArchiLogger.LogNullError(nameof(games));
				return false;
			}

			float maxHour = games.Max(game => game.HoursPlayed);
			if (maxHour < 0) {
				Bot.ArchiLogger.LogNullError(nameof(maxHour));
				return false;
			}

			if (maxHour >= HoursToBump) {
				Bot.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(maxHour)));
				return true;
			}

			Bot.IdleGames(games.Select(game => game.PlayableAppID));

			bool success = true;
			while (maxHour < 2) {
				Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.StillIdlingList, string.Join(", ", games.Select(game => game.AppID))));

				DateTime startFarmingPeriod = DateTime.UtcNow;
				if (FarmResetEvent.Wait(60 * 1000 * Program.GlobalConfig.FarmingDelay)) {
					FarmResetEvent.Reset();
					success = KeepFarming;
				}

				// Don't forget to update our GamesToFarm hours
				float timePlayed = (float) DateTime.UtcNow.Subtract(startFarmingPeriod).TotalHours;
				foreach (Game game in games) {
					game.HoursPlayed += timePlayed;
				}

				if (!success) {
					break;
				}

				maxHour += timePlayed;
			}

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.StoppedIdlingList, string.Join(", ", games.Select(game => game.AppID))));
			return success;
		}

		private bool FarmMultiple(IEnumerable<Game> games) {
			if (games == null) {
				Bot.ArchiLogger.LogNullError(nameof(games));
				return false;
			}

			CurrentGamesFarming.ReplaceWith(games);

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.NowIdlingList, string.Join(", ", CurrentGamesFarming.Select(game => game.AppID))));

			bool result = FarmHours(CurrentGamesFarming);
			CurrentGamesFarming.Clear();
			return result;
		}

		private async Task<bool> FarmSolo(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return true;
			}

			CurrentGamesFarming.Add(game);

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.NowIdling, game.AppID, game.GameName));

			bool result = await Farm(game).ConfigureAwait(false);
			CurrentGamesFarming.Clear();

			if (!result) {
				return false;
			}

			GamesToFarm.Remove(game);

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.IdlingFinishedForGame, game.AppID, game.GameName, TimeSpan.FromHours(game.HoursPlayed).ToHumanReadable()));
			return true;
		}

		private async Task<ushort?> GetCardsRemaining(uint appID) {
			if (appID == 0) {
				Bot.ArchiLogger.LogNullError(nameof(appID));
				return 0;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetGameCardsPage(appID).ConfigureAwait(false);

			HtmlNode progressNode = htmlDocument?.DocumentNode.SelectSingleNode("//span[@class='progress_info_bold']");
			if (progressNode == null) {
				return null;
			}

			string progress = progressNode.InnerText;
			if (string.IsNullOrEmpty(progress)) {
				Bot.ArchiLogger.LogNullError(nameof(progress));
				return null;
			}

			Match match = Regex.Match(progress, @"\d+");
			if (!match.Success) {
				return 0;
			}

			if (ushort.TryParse(match.Value, out ushort cardsRemaining) && (cardsRemaining != 0)) {
				return cardsRemaining;
			}

			Bot.ArchiLogger.LogNullError(nameof(cardsRemaining));
			return null;
		}

		private async Task<bool?> IsAnythingToFarm() {
			// Find the number of badge pages
			Bot.ArchiLogger.LogGenericInfo(Strings.CheckingFirstBadgePage);
			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetBadgePage(1).ConfigureAwait(false);
			if (htmlDocument == null) {
				Bot.ArchiLogger.LogGenericWarning(Strings.WarningCouldNotCheckBadges);
				return null;
			}

			byte maxPages = 1;

			HtmlNode htmlNode = htmlDocument.DocumentNode.SelectSingleNode("(//a[@class='pagelink'])[last()]");
			if (htmlNode != null) {
				string lastPage = htmlNode.InnerText;
				if (string.IsNullOrEmpty(lastPage)) {
					Bot.ArchiLogger.LogNullError(nameof(lastPage));
					return null;
				}

				if (!byte.TryParse(lastPage, out maxPages) || (maxPages == 0)) {
					Bot.ArchiLogger.LogNullError(nameof(maxPages));
					return null;
				}
			}

			GamesToFarm.Clear();

			List<Task> tasks = new List<Task>();
			Task mainTask = CheckPage(htmlDocument);

			switch (Program.GlobalConfig.OptimizationMode) {
				case GlobalConfig.EOptimizationMode.MinMemoryUsage:
					await mainTask.ConfigureAwait(false);
					break;
				default:
					tasks.Add(mainTask);
					break;
			}

			if (maxPages > 1) {
				Bot.ArchiLogger.LogGenericInfo(Strings.CheckingOtherBadgePages);

				switch (Program.GlobalConfig.OptimizationMode) {
					case GlobalConfig.EOptimizationMode.MinMemoryUsage:
						for (byte page = 2; page <= maxPages; page++) {
							await CheckPage(page).ConfigureAwait(false);
						}

						break;
					default:
						for (byte page = 2; page <= maxPages; page++) {
							// We need a copy of variable being passed when in for loops, as loop will proceed before our task is launched
							byte currentPage = page;
							tasks.Add(CheckPage(currentPage));
						}

						break;
				}
			}

			if (tasks.Count > 0) {
				await Task.WhenAll(tasks).ConfigureAwait(false);
			}

			if (GamesToFarm.Count == 0) {
				return false;
			}

			SortGamesToFarm();
			return true;
		}

		private async Task<bool> IsPlayableGame(Game game) {
			(uint PlayableAppID, DateTime IgnoredUntil) appData = await Bot.GetAppDataForIdling(game.AppID).ConfigureAwait(false);
			if (appData.PlayableAppID != 0) {
				game.PlayableAppID = appData.PlayableAppID;
				return true;
			}

			IgnoredAppIDs[game.AppID] = appData.IgnoredUntil != DateTime.MaxValue ? appData.IgnoredUntil : DateTime.UtcNow.AddHours(HoursToIgnore);
			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.IdlingGameNotPossible, game.AppID, game.GameName));
			return false;
		}

		private async Task<bool?> ShouldFarm(Game game) {
			if (game == null) {
				Bot.ArchiLogger.LogNullError(nameof(game));
				return false;
			}

			ushort? cardsRemaining = await GetCardsRemaining(game.AppID).ConfigureAwait(false);
			if (!cardsRemaining.HasValue) {
				Bot.ArchiLogger.LogGenericWarning(string.Format(Strings.WarningCouldNotCheckCardsStatus, game.AppID, game.GameName));
				return null;
			}

			game.CardsRemaining = cardsRemaining.Value;

			Bot.ArchiLogger.LogGenericInfo(string.Format(Strings.IdlingStatusForGame, game.AppID, game.GameName, game.CardsRemaining));
			return game.CardsRemaining > 0;
		}

		private void SortGamesToFarm() {
			IOrderedEnumerable<Game> gamesToFarm = GamesToFarm.OrderBy(game => Bot.IsPriorityIdling(game.AppID) ? 1 : 0);

			switch (Bot.BotConfig.FarmingOrder) {
				case BotConfig.EFarmingOrder.Unordered:
					break;
				case BotConfig.EFarmingOrder.AppIDsAscending:
					gamesToFarm = gamesToFarm.ThenBy(game => game.AppID);
					break;
				case BotConfig.EFarmingOrder.AppIDsDescending:
					gamesToFarm = gamesToFarm.ThenByDescending(game => game.AppID);
					break;
				case BotConfig.EFarmingOrder.BadgeLevelsAscending:
					gamesToFarm = gamesToFarm.ThenBy(game => game.BadgeLevel);
					break;
				case BotConfig.EFarmingOrder.BadgeLevelsDescending:
					gamesToFarm = gamesToFarm.ThenByDescending(game => game.BadgeLevel);
					break;
				case BotConfig.EFarmingOrder.CardDropsAscending:
					gamesToFarm = gamesToFarm.ThenBy(game => game.CardsRemaining);
					break;
				case BotConfig.EFarmingOrder.CardDropsDescending:
					gamesToFarm = gamesToFarm.ThenByDescending(game => game.CardsRemaining);
					break;
				case BotConfig.EFarmingOrder.HoursAscending:
					gamesToFarm = gamesToFarm.ThenBy(game => game.HoursPlayed);
					break;
				case BotConfig.EFarmingOrder.HoursDescending:
					gamesToFarm = gamesToFarm.ThenByDescending(game => game.HoursPlayed);
					break;
				case BotConfig.EFarmingOrder.NamesAscending:
					gamesToFarm = gamesToFarm.ThenBy(game => game.GameName);
					break;
				case BotConfig.EFarmingOrder.NamesDescending:
					gamesToFarm = gamesToFarm.ThenByDescending(game => game.GameName);
					break;
				case BotConfig.EFarmingOrder.Random:
					gamesToFarm = gamesToFarm.ThenBy(game => Utilities.RandomNext());
					break;
				case BotConfig.EFarmingOrder.RedeemDateTimesAscending:
				case BotConfig.EFarmingOrder.RedeemDateTimesDescending:
					Dictionary<uint, DateTime> redeemDates = new Dictionary<uint, DateTime>(GamesToFarm.Count);

					foreach (Game game in GamesToFarm) {
						DateTime redeemDate = DateTime.MinValue;
						if (Program.GlobalDatabase.AppIDsToPackageIDs.TryGetValue(game.AppID, out ConcurrentHashSet<uint> packageIDs)) {
							// ReSharper disable once LoopCanBePartlyConvertedToQuery - C# 7.0 out can't be used within LINQ query yet | https://github.com/dotnet/roslyn/issues/15619
							foreach (uint packageID in packageIDs) {
								if (!Bot.OwnedPackageIDs.TryGetValue(packageID, out (EPaymentMethod PaymentMethod, DateTime TimeCreated) packageData)) {
									continue;
								}

								if (packageData.TimeCreated > redeemDate) {
									redeemDate = packageData.TimeCreated;
								}
							}
						}

						redeemDates[game.AppID] = redeemDate;
					}

					switch (Bot.BotConfig.FarmingOrder) {
						case BotConfig.EFarmingOrder.RedeemDateTimesAscending:
							gamesToFarm = gamesToFarm.ThenBy(game => redeemDates[game.AppID]);
							break;
						case BotConfig.EFarmingOrder.RedeemDateTimesDescending:
							gamesToFarm = gamesToFarm.ThenByDescending(game => redeemDates[game.AppID]);
							break;
						default:
							Bot.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(Bot.BotConfig.FarmingOrder)));
							return;
					}

					break;
				default:
					Bot.ArchiLogger.LogGenericError(string.Format(Strings.ErrorIsInvalid, nameof(Bot.BotConfig.FarmingOrder)));
					return;
			}

			// We must call ToList() here as we can't enumerate during replacing
			GamesToFarm.ReplaceWith(gamesToFarm.ToList());
		}

		internal sealed class Game {
			[JsonProperty]
			internal readonly uint AppID;

			internal readonly byte BadgeLevel;

			[JsonProperty]
			internal readonly string GameName;

			[JsonProperty]
			internal ushort CardsRemaining { get; set; }

			[JsonProperty]
			internal float HoursPlayed { get; set; }

			internal uint PlayableAppID { get; set; }

			internal Game(uint appID, string gameName, float hoursPlayed, ushort cardsRemaining, byte badgeLevel) {
				if ((appID == 0) || string.IsNullOrEmpty(gameName) || (hoursPlayed < 0) || (cardsRemaining == 0)) {
					throw new ArgumentOutOfRangeException(nameof(appID) + " || " + nameof(gameName) + " || " + nameof(hoursPlayed) + " || " + nameof(cardsRemaining));
				}

				AppID = appID;
				GameName = gameName;
				HoursPlayed = hoursPlayed;
				CardsRemaining = cardsRemaining;
				BadgeLevel = badgeLevel;

				PlayableAppID = appID;
			}

			public override bool Equals(object obj) {
				if (ReferenceEquals(null, obj)) {
					return false;
				}

				if (ReferenceEquals(this, obj)) {
					return true;
				}

				Game game = obj as Game;
				return (game != null) && Equals(game);
			}

			public override int GetHashCode() => (int) AppID;

			private bool Equals(Game other) => AppID == other.AppID;
		}
	}
}