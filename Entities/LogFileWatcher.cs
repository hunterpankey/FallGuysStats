﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace FallGuysStats {
    public class LogLine {
        public TimeSpan Time { get; } = TimeSpan.Zero;
        public DateTime Date { get; set; } = DateTime.MinValue;
        public string Line { get; set; }
        public bool IsValid { get; set; }
        public long Offset { get; set; }

        public LogLine(string line, long offset) {
            Offset = offset;
            Line = line;
            IsValid = line.IndexOf(':') == 2 && line.IndexOf(':', 3) == 5 && line.IndexOf(':', 6) == 12;
            if (IsValid) {
                Time = TimeSpan.Parse(line.Substring(0, 12));
            }
        }

        public override string ToString() {
            return $"{Time}: {Line} ({Offset})";
        }
    }
    public class LogFileWatcher {
        const int UpdateDelay = 500;

        private string filePath;
        private string prevFilePath;
        private List<LogLine> lines = new List<LogLine>();
        private bool running;
        private bool stop;
        private Thread watcher, parser;

        public event Action<List<RoundInfo>> OnParsedLogLines;
        public event Action<List<RoundInfo>> OnParsedLogLinesCurrent;
        public event Action<DateTime> OnNewLogFileDate;
        public event Action<string> OnError;

        public void Start(string logDirectory, string fileName) {
            if (running) { return; }

            filePath = Path.Combine(logDirectory, fileName);
            prevFilePath = Path.Combine(logDirectory, Path.GetFileNameWithoutExtension(fileName) + "-prev.log");
            stop = false;
            watcher = new Thread(ReadLogFile) { IsBackground = true };
            watcher.Start();
            parser = new Thread(ParseLines) { IsBackground = true };
            parser.Start();
        }

        public async Task Stop() {
            stop = true;
            while (running || watcher == null || watcher.ThreadState == ThreadState.Unstarted) {
                await Task.Delay(50);
            }
            lines = new List<LogLine>();
            await Task.Factory.StartNew(() => watcher?.Join());
            await Task.Factory.StartNew(() => parser?.Join());
        }

        private void ReadLogFile() {
            running = true;
            List<LogLine> tempLines = new List<LogLine>();
            DateTime lastDate = DateTime.MinValue;
            bool completed = false;
            string currentFilePath = prevFilePath;
            long offset = 0;
            while (!stop) {
                try {
                    if (File.Exists(currentFilePath)) {
                        using (FileStream fs = new FileStream(currentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                            tempLines.Clear();

                            if (fs.Length > offset) {
                                fs.Seek(offset, SeekOrigin.Begin);

                                LineReader sr = new LineReader(fs);
                                string line;
                                DateTime currentDate = lastDate;
                                while ((line = sr.ReadLine()) != null) {
                                    LogLine logLine = new LogLine(line, sr.Position);

                                    if (logLine.IsValid) {
                                        int index;
                                        if ((index = line.IndexOf("[GlobalGameStateClient].PreStart called at ")) > 0) {
                                            currentDate = DateTime.SpecifyKind(DateTime.Parse(line.Substring(index + 43, 19)), DateTimeKind.Utc);
                                            OnNewLogFileDate?.Invoke(currentDate);
                                        }

                                        if (currentDate != DateTime.MinValue) {
                                            if ((int)currentDate.TimeOfDay.TotalSeconds > (int)logLine.Time.TotalSeconds) {
                                                currentDate = currentDate.AddDays(1);
                                            }
                                            currentDate = currentDate.AddSeconds(logLine.Time.TotalSeconds - currentDate.TimeOfDay.TotalSeconds);
                                            logLine.Date = currentDate;
                                        }

                                        if (line.IndexOf(" == [CompletedEpisodeDto] ==") > 0) {
                                            StringBuilder sb = new StringBuilder(line);
                                            sb.AppendLine();
                                            while ((line = sr.ReadLine()) != null) {
                                                LogLine temp = new LogLine(line, fs.Position);
                                                if (temp.IsValid) {
                                                    logLine.Line = sb.ToString();
                                                    logLine.Offset = sr.Position;
                                                    tempLines.Add(logLine);
                                                    tempLines.Add(temp);
                                                    break;
                                                } else if (!string.IsNullOrEmpty(line)) {
                                                    sb.AppendLine(line);
                                                }
                                            }
                                        } else {
                                            tempLines.Add(logLine);
                                        }
                                    } else if (logLine.Line.IndexOf("Client address: ", StringComparison.OrdinalIgnoreCase) > 0) {
                                        tempLines.Add(logLine);
                                    }
                                }
                            } else if (offset > fs.Length) {
                                offset = 0;
                            }
                        }
                    }

                    if (tempLines.Count > 0) {
                        RoundInfo stat = null;
                        List<RoundInfo> round = new List<RoundInfo>();
                        int players = 0;
                        bool countPlayers = false;
                        bool currentlyInParty = false;
                        bool privateLobby = false;
                        bool findPosition = false;
                        string currentPlayerID = string.Empty;
                        int lastPing = 0;
                        int gameDuration = 0;
                        for (int i = 0; i < tempLines.Count; i++) {
                            LogLine line = tempLines[i];
                            if (ParseLine(line, round, ref currentPlayerID, ref countPlayers, ref currentlyInParty, ref findPosition, ref players, ref stat, ref lastPing, ref gameDuration, ref privateLobby)) {
                                lastDate = line.Date;
                                offset = line.Offset;
                                lock (lines) {
                                    lines.AddRange(tempLines);
                                    tempLines.Clear();
                                }
                                break;
                            } else if (line.Line.IndexOf("[StateMatchmaking] Begin matchmaking", StringComparison.OrdinalIgnoreCase) > 0 ||
                                line.Line.IndexOf("[GameStateMachine] Replacing FGClient.StateMainMenu with FGClient.StatePrivateLobby", StringComparison.OrdinalIgnoreCase) > 0) {
                                offset = i > 0 ? tempLines[i - 1].Offset : offset;
                                lastDate = line.Date;
                            }
                        }

                        if (lastPing != 0) {
                            Stats.LastServerPing = lastPing;
                        }
                        OnParsedLogLinesCurrent?.Invoke(round);
                    }

                    if (!completed) {
                        completed = true;
                        offset = 0;
                        currentFilePath = filePath;
                    }
                } catch (Exception ex) {
                    OnError?.Invoke(ex.ToString());
                }
                Thread.Sleep(UpdateDelay);
            }
            running = false;
        }
        private void ParseLines() {
            RoundInfo stat = null;
            List<RoundInfo> round = new List<RoundInfo>();
            List<RoundInfo> allStats = new List<RoundInfo>();
            int players = 0;
            bool countPlayers = false;
            bool currentlyInParty = false;
            bool privateLobby = false;
            bool findPosition = false;
            string currentPlayerID = string.Empty;
            int lastPing = 0;
            int gameDuration = 0;
            while (!stop) {
                try {
                    lock (lines) {
                        for (int i = 0; i < lines.Count; i++) {
                            LogLine line = lines[i];
                            if (ParseLine(line, round, ref currentPlayerID, ref countPlayers, ref currentlyInParty, ref findPosition, ref players, ref stat, ref lastPing, ref gameDuration, ref privateLobby)) {
                                allStats.AddRange(round);
                            }
                        }

                        if (allStats.Count > 0) {
                            OnParsedLogLines?.Invoke(allStats);
                            allStats.Clear();
                        }

                        lines.Clear();
                    }
                } catch (Exception ex) {
                    OnError?.Invoke(ex.ToString());
                }
                Thread.Sleep(UpdateDelay);
            }
        }
        private bool ParseLine(LogLine line, List<RoundInfo> round, ref string currentPlayerID, ref bool countPlayers, ref bool currentlyInParty, ref bool findPosition, ref int players, ref RoundInfo stat, ref int lastPing, ref int gameDuration, ref bool privateLobby) {
            int index;
            if ((index = line.Line.IndexOf("[StateGameLoading] Loading game level scene", StringComparison.OrdinalIgnoreCase)) > 0) {
                stat = new RoundInfo();
                int index2 = line.Line.IndexOf(' ', index + 44);
                if (index2 < 0) { index2 = line.Line.Length; }

                stat.SceneName = line.Line.Substring(index + 44, index2 - index - 44);
                findPosition = false;
                round.Add(stat);
            } else if (stat != null && (index = line.Line.IndexOf("[StateGameLoading] Finished loading game level", StringComparison.OrdinalIgnoreCase)) > 0) {
                int index2 = line.Line.IndexOf(". ", index + 62);
                if (index2 < 0) { index2 = line.Line.Length; }

                stat.Name = line.Line.Substring(index + 62, index2 - index - 62);
                stat.Round = round.Count;
                stat.Start = line.Date;
                stat.InParty = currentlyInParty;
                stat.PrivateLobby = privateLobby;
                stat.GameDuration = gameDuration;
                countPlayers = true;
            } else if ((index = line.Line.IndexOf("[StateMatchmaking] Begin matchmaking", StringComparison.OrdinalIgnoreCase)) > 0 ||
                (index = line.Line.IndexOf("[GameStateMachine] Replacing FGClient.StateMainMenu with FGClient.StatePrivateLobby", StringComparison.OrdinalIgnoreCase)) > 0) {
                privateLobby = line.Line.IndexOf("StatePrivateLobby") > 0;
                currentlyInParty = privateLobby || !line.Line.Substring(index + 37).Equals("solo", StringComparison.OrdinalIgnoreCase);
                if (stat != null) {
                    if (stat.End == DateTime.MinValue) {
                        stat.End = line.Date;
                    }
                    stat.Playing = false;
                }
                findPosition = false;
                Stats.InShow = true;
                round.Clear();
                stat = null;
            } else if ((index = line.Line.IndexOf("NetworkGameOptions: durationInSeconds=", StringComparison.OrdinalIgnoreCase)) > 0) {
                int nextIndex = line.Line.IndexOf(" ", index + 38);
                gameDuration = int.Parse(line.Line.Substring(index + 38, nextIndex - index - 38));
            } else if (stat != null && countPlayers && line.Line.IndexOf("[ClientGameManager] Added player ", StringComparison.OrdinalIgnoreCase) > 0 && (index = line.Line.IndexOf(" players in system.", StringComparison.OrdinalIgnoreCase)) > 0) {
                int prevIndex = line.Line.LastIndexOf(' ', index - 1);
                if (int.TryParse(line.Line.Substring(prevIndex, index - prevIndex), out players)) {
                    stat.Players = players;
                }
            } else if ((index = line.Line.IndexOf("[ClientGameManager] Handling bootstrap for local player FallGuy [", StringComparison.OrdinalIgnoreCase)) > 0) {
                int prevIndex = line.Line.IndexOf(']', index + 65);
                currentPlayerID = line.Line.Substring(index + 65, prevIndex - index - 65);
            } else if (stat != null && line.Line.IndexOf($"[ClientGameManager] Handling unspawn for player FallGuy [{currentPlayerID}]", StringComparison.OrdinalIgnoreCase) > 0) {
                if (stat.End == DateTime.MinValue) {
                    stat.Finish = line.Date;
                } else {
                    stat.Finish = stat.End;
                }
                findPosition = true;
            } else if (stat != null && findPosition && (index = line.Line.IndexOf("[ClientGameSession] NumPlayersAchievingObjective=")) > 0) {
                int position = int.Parse(line.Line.Substring(index + 49));
                if (position > 0) {
                    findPosition = false;
                    stat.Position = position;
                }
            } else if (stat != null && line.Line.IndexOf("Client address: ", StringComparison.OrdinalIgnoreCase) > 0) {
                index = line.Line.IndexOf("RTT: ");
                if (index > 0) {
                    int msIndex = line.Line.IndexOf("ms", index);
                    lastPing = int.Parse(line.Line.Substring(index + 5, msIndex - index - 5));
                }
            } else if (stat != null && line.Line.IndexOf("[GameSession] Changing state from Countdown to Playing", StringComparison.OrdinalIgnoreCase) > 0) {
                stat.Start = line.Date;
                stat.Playing = true;
                countPlayers = false;
            } else if (stat != null &&
                (line.Line.IndexOf("[GameSession] Changing state from Playing to GameOver", StringComparison.OrdinalIgnoreCase) > 0
                || line.Line.IndexOf("Changing local player state to: SpectatingEliminated", StringComparison.OrdinalIgnoreCase) > 0
                || line.Line.IndexOf("[GlobalGameStateClient] SwitchToDisconnectingState", StringComparison.OrdinalIgnoreCase) > 0
                || line.Line.IndexOf("[GameStateMachine] Replacing FGClient.StatePrivateLobby with FGClient.StateMainMenu", StringComparison.OrdinalIgnoreCase) > 0)) {
                if (stat.End == DateTime.MinValue) {
                    stat.End = line.Date;
                }
                stat.Playing = false;
            } else if (line.Line.IndexOf("[StateMainMenu] Loading scene MainMenu", StringComparison.OrdinalIgnoreCase) > 0) {
                if (stat != null) {
                    if (stat.End == DateTime.MinValue) {
                        stat.End = line.Date;
                    }
                    stat.Playing = false;
                }
                findPosition = false;
                countPlayers = false;
                Stats.InShow = false;
            } else if (line.Line.IndexOf(" == [CompletedEpisodeDto] ==", StringComparison.OrdinalIgnoreCase) > 0) {
                if (stat == null) { return false; }

                RoundInfo temp = null;
                StringReader sr = new StringReader(line.Line);
                string detail;
                bool foundRound = false;
                int maxRound = 0;
                DateTime showStart = DateTime.MinValue;
                while ((detail = sr.ReadLine()) != null) {
                    if (detail.IndexOf("[Round ", StringComparison.OrdinalIgnoreCase) == 0) {
                        foundRound = true;
                        int roundNum = (int)detail[7] - 0x30 + 1;
                        string roundName = detail.Substring(11, detail.Length - 12);

                        if (roundNum - 1 < round.Count) {
                            if (roundNum > maxRound) {
                                maxRound = roundNum;
                            }

                            temp = round[roundNum - 1];
                            if (string.IsNullOrEmpty(temp.Name) || !temp.Name.Equals(roundName, StringComparison.OrdinalIgnoreCase)) {
                                return false;
                            }

                            temp.VerifyName();
                            if (roundNum == 1) {
                                showStart = temp.Start;
                            }
                            temp.ShowStart = showStart;
                            temp.Playing = false;
                            temp.Round = roundNum;
                            privateLobby = temp.PrivateLobby;
                            currentlyInParty = temp.InParty;
                        } else {
                            return false;
                        }

                        if (temp.End == DateTime.MinValue) {
                            temp.End = line.Date;
                        }
                        if (temp.Start == DateTime.MinValue) {
                            temp.Start = temp.End;
                        }
                        if (!temp.Finish.HasValue) {
                            temp.Finish = temp.End;
                        }
                    } else if (foundRound) {
                        if (detail.IndexOf("> Position: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Position = int.Parse(detail.Substring(12));
                        } else if (detail.IndexOf("> Team Score: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Score = int.Parse(detail.Substring(14));
                        } else if (detail.IndexOf("> Qualified: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            char qualified = detail[13];
                            temp.Qualified = qualified == 'T';
                            temp.Finish = temp.Qualified ? temp.Finish : null;
                        } else if (detail.IndexOf("> Bonus Tier: ", StringComparison.OrdinalIgnoreCase) == 0 && detail.Length == 15) {
                            char tier = detail[14];
                            temp.Tier = (int)tier - 0x30 + 1;
                        } else if (detail.IndexOf("> Kudos: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Kudos += int.Parse(detail.Substring(9));
                        } else if (detail.IndexOf("> Bonus Kudos: ", StringComparison.OrdinalIgnoreCase) == 0) {
                            temp.Kudos += int.Parse(detail.Substring(15));
                        }
                    }
                }

                if (round.Count > maxRound) {
                    return false;
                }

                stat = round[round.Count - 1];
                DateTime showEnd = stat.End;
                for (int i = 0; i < round.Count; i++) {
                    round[i].ShowEnd = showEnd;
                }
                if (stat.Qualified) {
                    stat.Crown = true;
                }
                stat = null;
                Stats.InShow = false;
                Stats.EndedShow = true;
                return true;
            }
            return false;
        }
    }
}