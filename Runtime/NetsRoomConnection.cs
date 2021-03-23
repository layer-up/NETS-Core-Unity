using Newtonsoft.Json;
using Odessa.Nets.Core;
using Odessa.Nets.Core.Models;
using Odessa.Nets.EntityTracking;
using Odessa.Nets.EntityTracking.EventObjects;
using Odessa.Nets.EntityTracking.EventObjects.NativeEvents;
using Odessa.Nets.EntityTracking.EventObjects.NativeTypes;
using Odessa.Nets.EntityTracking.Wrappers;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace OdessaEngine.NETS.Core {
    public class NetsRoomConnection : IListenToEventApplication {

        public Action<Guid, int> OnPlayersInRoomLeft;
        public Action<Guid, int> OnPlayersInRoomJoined;

        public Action<bool> OnConnect;
        public Action<bool> OnIsServer;
        public Action<int> OnPlayerCountChange; // TODO


        public bool IsServer = false;
        public Guid? myAccountGuid { get; set; } = null;
        public Guid? roomGuid = null;

        WebSocket w;

        public bool canSend => w?.isConnected == true;


        bool oldConnected = false;

        public Dictionary<string, NetsEntity> KnownServerSingletons = new Dictionary<string, NetsEntity>();


        private NETSSettings settings => NETSSettings.instance;
        private MonoBehaviour monoRef = NetsNetworking.instance;
        void print(string s) => MonoBehaviour.print(s);
        Coroutine StartCoroutine(IEnumerator e) => monoRef.StartCoroutine(e);

        public NetsRoomConnection(RoomState roomState) {
            myAccountGuid = RoomServiceUtils.GetMyAccountGuid();

            var protocol = roomState.ip.Contains(":125") ? "wss" : "ws";
            StartCoroutine(connect($"{protocol}://{roomState.ip}"));
            StartCoroutine(WaitUntilConnected(() => {
                var sendData = BitUtils.ArrayFromStream(bos => {
                    bos.WriteByte((byte)ClientToWorkerMessageType.JoinRoom);
                    bos.WriteString(roomState.token);
                });
                w.Send(sendData);
            }));

        }

        IEnumerator connect(string url) {
            intentionallyDisconnected = false;
            if (connected) {
                if (settings.DebugConnections)  print("Already connected");
                yield break;
            }
            WebSocket conn = new WebSocket(new Uri(url));
            if (settings.DebugConnections) print("Attempting connection to game server on " + url);

            if (w != null || connected) {
                if (settings.DebugConnections) print("Closing websocket");
                conn.Close();
                yield return new WaitForSeconds(0.02f);
            }

            conn = new WebSocket(new Uri(url));

            if (settings.DebugConnections) print("waiting for ready");
            while (conn.isReady == false) {
                yield return new WaitForSeconds(.03f); // Wait for iframe to postback
            }

            if (settings.DebugConnections) print("attempting connect");
            yield return StartCoroutine(conn.Connect());
            if (settings.DebugConnections) print("Connected to game server? " + conn.isConnected);
            yield return new WaitForSeconds(.2f);

            bool valid = true;
            if (!conn.isConnected) {
                yield return new WaitForSeconds(.02f);
                valid = false;
                if (intentionallyDisconnected) yield break;
                if (settings.DebugConnections) print("Attempting reconnect...");
                if (!connected)
                    StartCoroutine(connect(url));
                yield break;
            }
            if (connected) {
                if (settings.DebugConnections) print("Too late for " + url);
                conn.Close(); // Too late
                yield break;
            }

            w = conn;
            connected = true;
            room = new RoomModel();
            entityIdToNetsEntity.Clear();
            StartCoroutine(SendChanges());
            if (settings.DebugConnections) print("Debug: valid: " + valid + " , connected " + connected);

            //listener.OnConnected();
            HasJoinedRoom = false;
            initializedSingletons = false;
            //foreach (var ent in GetAllNetsEntitysOfTypes(AuthorityEnum.ServerSingleton)) {
            //    Destroy(ent.gameObject);
            //}
            KnownServerSingletons.Clear();
            while (valid) {
                if (!w.isConnected) {
                    if (!intentionallyDisconnected) print("ws error!");
                    valid = false;
                    connected = false;
                    oldConnected = false;
                    OnConnect?.Invoke(false);
                    if (intentionallyDisconnected) yield break;
                    StartCoroutine(connect(url));
                    yield break;
                }

                if (w.isConnected != oldConnected)
                    OnConnect?.Invoke(w.isConnected);
                oldConnected = w.isConnected;

                byte[] data = w.Recv();
                if (data == null) {
                    yield return new WaitForSeconds(.03f); // Basically just yield to other threads, checking 30 times a sec
                    continue;
                }
                if (settings.DebugLatencyMs > 0)
                    yield return HandlePacketWithDelay(data);
                else
                    HandlePacket(data);

            }
            OnConnect?.Invoke(false);
            w.Close();
        }

        public List<EntityModel> Players() {
            return room.ClientConnectionModels.Values.ToList();
        }

        bool intentionallyDisconnected = false;
        private void Disconnect() {
            Debug.Log("On Disconnect from NETS");
            intentionallyDisconnected = true;
            w.Close();

            foreach (var e in entityIdToNetsEntity.Values) {
                e.MarkAsDestroyedByServer(); // Avoid throwing
                KnownServerSingletons.Remove(e.prefab);
                if (e && e.gameObject)
                    MonoBehaviour.Destroy(e.gameObject);
            }

            entityIdToNetsEntity.Clear();
            room = new RoomModel();
            KnownServerSingletons.Clear();

            foreach (var e in MonoBehaviour.FindObjectsOfType<NetsEntity>()) {
                e.MarkAsDestroyedByServer(); // Avoid throwing
                MonoBehaviour.Destroy(e.gameObject);
            }
            LeaveRoom();
        }


        bool connected = false;
        public RoomModel room;
        public Dictionary<string, NetsEntity> entityIdToNetsEntity = new Dictionary<string, NetsEntity>();

        bool initializedSingletons = false;
        bool HasJoinedRoom = false;
        IEnumerator HandlePacketWithDelay(byte[] data) {
            if (settings.DebugLatencyMs > 0)
                yield return new WaitForSeconds(settings.DebugLatencyMs / 1000f);
            HandlePacket(data);
        }

        public void OnEntityDestroyed(EntityModel entity) {
            try {
                if (settings.DebugConnections) print($"Removed {entity.uniqueId} {entity.PrefabName}");
                if (entityIdToNetsEntity.TryGetValue(entity.uniqueId, out var e) && e != null && e.gameObject != null) {
                    e.MarkAsDestroyedByServer();
                    MonoBehaviour.Destroy(e.gameObject);
                }
                entityIdToNetsEntity.Remove(entity.uniqueId);
                /*
                if (entity.PrefabName == "?ClientConnection") {
                    OnPlayersInRoomLeft?.Invoke(Guid.ParseExact(entity.GetString("AccountGuid"), "N"), Players().Count);
                }
                */
            } catch (Exception e) {
                Debug.LogError(e);
            }
        }

        public void OnEntityCreated(EntityModel entity) {
            try {
                if (settings.DebugConnections) print($"Created entity {entity.uniqueId}: {entity.PrefabName}");

                entityIdToNetsEntity.TryGetValue(entity.uniqueId, out var matchedEntity);
                if (matchedEntity != null) {
                    entityIdToNetsEntity[entity.uniqueId] = matchedEntity;
                    matchedEntity.OnCreatedOnServer(roomGuid.Value, entity);
                    matchedEntity.Owner = entity.Owner;
                }
                /*
                if (entity.PrefabName == "?ClientConnection") {
                    OnPlayersInRoomJoined?.Invoke(Guid.ParseExact(entity.GetString("AccountGuid"), "N"), Players().Count);
                }
                */
                NETSNetworkedTypesLists.instance.NetworkedTypesLookup.TryGetValue(entity.PrefabName, out var typeToCreate);
                if (typeToCreate == null) {
                    if (entity.PrefabName.Contains("?") == false)
                        print("Unable to find object " + entity.uniqueId+ " " + entity.PrefabName);
                }

                if (IsServer && KnownServerSingletons.ContainsKey(typeToCreate.name)) {
                    //DestroyEntity(entity);
                    throw new Exception($"Did not create new {typeToCreate.name} as we are server and already have one!");
                }

                typeToCreate.prefab.SetActive(false);
                var newGo = MonoBehaviour.Instantiate(typeToCreate.prefab, new Vector3(0, 0, 0), Quaternion.Euler(0, 0, 0));
                typeToCreate.prefab.SetActive(true);

                newGo.GetComponentsInChildren<NetsBehavior>().ToList().ForEach(b => b.TryInitialize());
                var component = newGo.GetComponent<NetsEntity>();
                if (component.Authority == AuthorityEnum.ServerSingleton) KnownServerSingletons[typeToCreate.name] = component;
                entityIdToNetsEntity[entity.uniqueId] = component;
                component.OnCreatedOnServer(roomGuid.Value, entity);
                newGo.SetActive(true);
            } catch (Exception e) {
                Debug.LogWarning(e);
            }
        }

        public void AfterEventsApplied() {
            try {
                if (IsServer && !initializedSingletons) {
                    foreach (var s in NETSNetworkedTypesLists.instance.ServerSingletonsList)
                        if (KnownServerSingletons.ContainsKey(s.name) == false) {
                            Debug.Log("Init prefab for singleton");
                            MonoBehaviour.Instantiate(s.prefab);
                        }
                    initializedSingletons = true;
                }
                if (HasJoinedRoom == false) {
                    HasJoinedRoom = true;
                }
            } catch (Exception e) {
                Debug.LogError(e);
            }
        }

        public void OnEntityPropertyChanged(EntityModel entity, string key) {
            try {
                //print($"room: {roomGuid:N} Updated {entity.Id}.{entity.PrefabName}: [{field.Name}] => {field.Value}");
                /*
                if (entity.Id == 1) {
                    if (entity.PrefabName != "?Room") throw new Exception("Expected room as entity ID 1");
                    if (field.Name == "ServerAccount") {
                        IsServer = myAccountGuid == Guid.ParseExact(entity.GetString("ServerAccount"), "N");
                        if (settings.DebugConnections) print($"ServerAccount: {entity.GetString("ServerAccount")} ({(IsServer ? "" : "not ")}me)");

                        if (IsServer == false) {
                            var startingEnts = NetsNetworking.FindObjectsOfType<NetsEntity>();
                            var localServerEntities = startingEnts
                                .Where(e => e.Authority.IsServerOwned() && e.Id == 0)
                                .ToList();
                            if (settings.DebugConnections) {
                                print($"Found {localServerEntities.Count} server entities to destroy as we are not server. {string.Join(",", localServerEntities.Select(e => e.prefab))}");
                            }
                            localServerEntities.ForEach(e => {
                                var comp = e.GetComponent<NetsEntity>();
                                comp.MarkAsDestroyedByServer(); // Avoid throwing
                                KnownServerSingletons.Remove(comp.prefab);
                                Destroy(e.gameObject);
                            });
                        }
                        OnIsServer?.Invoke(IsServer);
                    }
                    return;
                }
                */
                if (entityIdToNetsEntity.TryGetValue(entity.uniqueId, out var e)) {
                    e.OnFieldChange(entity.Fields, key);
                } else {
                    print("Unknown entity: " + entity.uniqueId);
                }
            } catch (Exception e) {
                Debug.LogError(e);
            }
        }


        void HandlePacket(byte[] data) {
            try {
                var bb = new BitBuffer(data);
                var category = bb.getByte();
                //if (category != (byte)ProxyToUnityMessageType.Ping)
                //print($"Got category: {category}");

                if (category == (byte)WorkerToClientMessageType.Ping) {
                    Guid requestId = bb.ReadGuid();
                    SendPong(requestId);
                } else if (category == (byte)WorkerToClientMessageType.JoinedRoom) {
                    roomGuid = bb.ReadGuid();
                    myAccountGuid = bb.ReadGuid();
                    if (settings.DebugConnections) Debug.Log($"NETS - Joined Room ID {roomGuid}");
                    room = new RoomModel();
                } else if (category == (byte)WorkerToClientMessageType.EntityEvent) {
                    //print($"Got entity change for room {roomGuid:N}");
                    try {
                        var rootEvent = NativeEventUtils.DeserializeNativeEventType(bb);
                        if (settings.DebugConnections) Debug.Log($"Applying event: {JsonConvert.SerializeObject(rootEvent, Formatting.Indented)}");
                        if (room == null) Debug.Log($"Room is null - whoa!");
                        room.ApplyRootEvent(rootEvent, this);
                        // After apply events
                        /*
                        OnEntityDestroyed
                        OnEntityCreated
                        OnEntityPropertyChanged
                        */
                    } catch (Exception e) {
                        Debug.LogError(e);
                    }
                } else if (category == (byte)WorkerToClientMessageType.RoomEvent) {
                    var accountGuid = bb.ReadGuid();
                    var eventString = bb.ReadString();
                } else if (category == (byte)WorkerToClientMessageType.EntityRpc) {
                    var senderAccountGuid = bb.ReadGuid();
                    var entityId = bb.ReadMaybeLimitedString();
                    var eventString = bb.ReadString();
                    try {
                        entityIdToNetsEntity.TryGetValue(entityId, out var entity);
                        if (entity == null) {
                            Debug.LogError($"Nets Entity doesn't exist. Room {roomGuid:N}, Entity{entityId}");
                            return;
                        }
                        entityIdToNetsEntity[entityId].InterpretMethod(eventString);
                    } catch (Exception e) {
                        Debug.LogError(e);
                    }
                }
            } catch (Exception e) {
                OnConnect?.Invoke(false);
                Debug.LogError(e);
            }
        }

        private IEnumerator DelayPing(Guid requestId) {
            yield return new WaitForSeconds(1);
            w.Send(BitUtils.ArrayFromStream(bos => {
                bos.WriteByte((byte)ClientToWorkerMessageType.Pong);
                bos.WriteGuid(requestId);
            }));
        }
        public void SendPong(Guid requestId) {
#if UNITY_EDITOR
            if (Application.isPlaying == false) return;
#endif
            w.Send(BitUtils.ArrayFromStream(bos => {
                bos.WriteByte((byte)ClientToWorkerMessageType.Pong);
                bos.WriteGuid(requestId);
            }));
        }

        public void SendRoomEvent(Guid roomGuid, string eventString = "") {
            w.Send(BitUtils.ArrayFromStream(bos => {
                bos.WriteByte((byte)ClientToWorkerMessageType.RoomEvent);
                bos.WriteString(eventString);
            }));
        }
        public void SendEntityEvent(NetsEntity entity, string eventString = "") {
            w.Send(BitUtils.ArrayFromStream(bos => {
                bos.WriteByte((byte)ClientToWorkerMessageType.EntityRpc);
                bos.WriteMaybeLimitedString(entity.Model().uniqueId);
                bos.WriteString(eventString);
            }));
        }

        float sendDelay = 0.2f;
        IEnumerator SendChanges() {
            while (w.isConnected) {
                yield return new WaitForSeconds(sendDelay);

                var changes = room.FlushEvents();
                if (changes == null) continue;

                if (settings.DebugConnections) Debug.Log($"Sending event: {JsonConvert.SerializeObject(changes, Formatting.Indented)}");

                w.Send(BitUtils.ArrayFromStream(bos => {
                    bos.WriteByte((byte)ClientToWorkerMessageType.EntityEvent);
                    changes.Serialize(bos, EntitySerializationContext.Admin);
                }));
            }
        }

        private IEnumerator WaitUntilConnected(Action action) {
            while (!connected)
                yield return new WaitForEndOfFrame();
            action?.Invoke();
        }

        public void LeaveRoom() {
            var requestGuid = Guid.NewGuid();
            try {
                w.Send(BitUtils.ArrayFromStream(bos => {
                    bos.WriteByte((byte)ClientToWorkerMessageType.LeaveRoom);
                    bos.WriteGuid(requestGuid);
                }));
            } catch (Exception e) {
                Debug.LogError(e);
            }
            Disconnect();
        }





    }
}