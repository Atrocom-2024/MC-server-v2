﻿using System.Collections.Concurrent;

using MC_server.GameRoom.Managers.Models;
using MC_server.GameRoom.Utils;
using MC_server.GameRoom.Service;
using MC_server.GameRoom.Models;
using MC_server.GameRoom.Communication;

namespace MC_server.GameRoom.Managers
{
    public class GameRoomManager
    {
        // 각 게임 룸의 현재 세션 정보를 관리 -> 키는 룸 id, 값은 해당 룸의 세션 데이터
        private readonly ConcurrentDictionary<int, GameSession> _gameSessions = new ConcurrentDictionary<int, GameSession>();
        // 각 룸별 타이머 관리
        private readonly ConcurrentDictionary<int, Timer> _sessionTimers = new ConcurrentDictionary<int, Timer>();

        private readonly ClientManager _clientManager;
        private readonly BroadcastMessageSender _broadcastMessageSender;
        private readonly GameTcpService _gameTcpService;

        private readonly object _lock = new object();

        public GameRoomManager(ClientManager clientManager, BroadcastMessageSender broadcastMessageSender, GameTcpService gameTcpService)
        {
            _clientManager = clientManager ?? throw new ArgumentNullException(nameof(clientManager));
            _broadcastMessageSender = broadcastMessageSender ?? throw new ArgumentNullException(nameof(broadcastMessageSender));
            _gameTcpService = gameTcpService ?? throw new ArgumentNullException(nameof(gameTcpService));
        }

        /// <summary>
        /// 특정 룸 세션의 잭팟 상태 변경 메서드
        /// </summary>
        /// <param name="roomId"></param>
        /// <param name="jackpotState"></param>
        public void ChangedJackpotState(int roomId, bool jackpotState)
        {
            _gameSessions.AddOrUpdate(roomId,
                key => new GameSession { IsJackpot = jackpotState }, // 값이 없으면 추가
                (key, existingGameSession) =>
                {
                    existingGameSession.IsJackpot = jackpotState; // 값이 있으면 업데이트
                    return existingGameSession;
                }
            );
        }

        /// <summary>
        /// 게임 세션 데이터 복사 메서드
        /// </summary>
        /// <param name="roomId"></param>
        /// <returns></returns>
        public GameSession CloneGameSession(int roomId)
        {
            return new GameSession
            {
                GameId = _gameSessions[roomId].GameId,
                TotalBetAmount = _gameSessions[roomId].TotalBetAmount,
                TotalUser = _gameSessions[roomId].TotalUser,
                TotalJackpotAmount = _gameSessions[roomId].TotalJackpotAmount,
                IsJackpot = _gameSessions[roomId].IsJackpot,
                TargetPayout = _gameSessions[roomId].TargetPayout,
                MaxBetAmount = _gameSessions[roomId].MaxBetAmount,
                MaxUser = _gameSessions[roomId].MaxUser,
                BaseJackpotAmount = _gameSessions[roomId].BaseJackpotAmount,
                CreatedAt = _gameSessions[roomId].CreatedAt
            };
        }
        
        /// <summary>
        /// 모든 룸 세션을 초기화 메서드
        /// </summary>
        /// <returns></returns>
        public async Task InitializeRooms()
        {
            // DB에서 룸의 기본 정보를 모두 받아옴
            var allRooms = await _gameTcpService.GetAllRoomsAsync();

            // 데이터 출력
            if (allRooms != null && allRooms.Count > 0)
            {
                foreach (var room in allRooms)
                {
                    var gameRecord = await _gameTcpService.GetGameRecordByIdAsync(room.RoomId);

                    // 게임 세션 초기화
                    _gameSessions[room.RoomId] = GameSessionUtils.CreateNewSession(room);

                    if (gameRecord != null)
                    {
                        _gameSessions[room.RoomId].TotalJackpotAmount = gameRecord.TotalJackpotAmount;
                    }

                    // 타이머 초기화
                    StartRoomTimer(room.RoomId);
                }
            }
            Console.WriteLine("[socket] Initialized 10 game rooms");
        }

        /// <summary>
        /// 특정 룸 세션 타이머 시작
        /// </summary>
        /// <param name="roomId"></param>
        public void StartRoomTimer(int roomId)
        {
            if (_sessionTimers.ContainsKey(roomId))
            {
                Console.WriteLine($"[socket] Timer for Room {roomId} is already running.");
                return;
            }

            _sessionTimers.AddOrUpdate(roomId, new Timer(async _ =>
            {
                // 잭팟이 터지거나 시간이 만료되면 초기화
                await ResetGameRoom(roomId);
            }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1)), (key, oldValue) => oldValue);

            Console.WriteLine($"[socket] Timer started for Room {roomId}");
        }

        /// <summary>
        /// 특정 룸 세션 초기화 메서드
        /// </summary>
        /// <param name="roomId"></param>
        /// <returns></returns>
        public async Task ResetGameRoom(int roomId)
        {
            // TODO: 잭팟이 터졌을 때 해당 룸 세션 초기화 기능 -> 다른 유저들에겐 TotalBetAmount의 10% 반환 후 페이아웃은 반환되지 않고 초기화
            Console.WriteLine($"[socket] Room {roomId}: Resetting session");

            // 잭팟으로 인한 초기화 로직 추가 가능
            var clientsInRoom = _clientManager.GetClientsInRoom(roomId);
            var room = await _gameTcpService.GetRoomByIdAsync(roomId);
            var tempGameSession = CloneGameSession(roomId); // 게임 세션 데이터 복사

            // 1. 게임 세션 초기화 -> IsJackpot이 false이면 기존의 잭팟 금액 유지
            if (room != null)
            {
                _gameSessions[roomId] = GameSessionUtils.CreateNewSession(room);
                _gameSessions[roomId].TotalUser = clientsInRoom.Count();

                if (!tempGameSession.IsJackpot) // 잭팟이 터지지 않았을 때
                {
                    _gameSessions[roomId].TotalJackpotAmount = tempGameSession.TotalJackpotAmount;
                }

                // 2. 세션 종료 리워드 브로드캐스트
                await _broadcastMessageSender.BroadcastGameSessionEnd(roomId);

                try
                {
                    var gameSession = GetGameSession(roomId);

                    // 3. 게임 유저 초기화 및 브로드캐스트
                    foreach (var client in clientsInRoom)
                    {
                        await _clientManager.ResetGameUser(client, gameSession);
                    }
                    await _broadcastMessageSender.BroadcastUserState(roomId); // 유저 상태 브로드캐스트

                    // 4. 게임 상태 브로드캐스트
                    var gameState = new GameState
                    {
                        TotalJackpotAmount = gameSession.TotalJackpotAmount,
                        IsJackpot = gameSession.IsJackpot
                    };
                    await _broadcastMessageSender.BroadcastGameState(roomId, gameState);

                    // 5. 게임이 초기화 될 때 초기화될 게임 세션의 데이터를 저장 -> 게임 결과 기록 목적
                    await _gameTcpService.RecordGameResult(roomId, tempGameSession);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[socket][ResetGameRoom] Error broadcasing to client: {ex.Message}");
                }

                // 타이머 재시작
                StopRoomTimer(roomId);
                StartRoomTimer(roomId);
            }
            else
            {
                Console.WriteLine($"[socket] Room {roomId} does not exist in session data.");
            }
        }

        /// <summary>
        /// 특정 룸 세션 타이머 중단 메서드
        /// </summary>
        /// <param name="roomId"></param>
        public void StopRoomTimer(int roomId)
        {
            if (_sessionTimers.TryRemove(roomId, out var timer))
            {
                timer.Dispose();
            }
            else
            {
                Console.WriteLine($"[socket] No active timer found for Room {roomId}");
            }
        }

        public void IncrementTotalUser(int roomId)
        {
            var gameSession = GetGameSession(roomId);

            if (gameSession != null)
            {
                gameSession.TotalUser++;
                Console.WriteLine($"[socket] Room {roomId}: Total users updated to {gameSession.TotalUser}");
            }
        }

        public GameSession GetGameSession(int roomId)
        {
            _gameSessions.TryGetValue(roomId, out var gameSession);

            if (gameSession == null)
            {
                throw new Exception($"[socket] Room {roomId} does not exist in session data.");
            }

            return gameSession;
        }

        public ConcurrentDictionary<int, GameSession> GetAllSessions()
        {
            return _gameSessions;
        }
    }
}
