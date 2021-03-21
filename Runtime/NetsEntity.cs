﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Odessa.Nets.EntityTracking;
using System.Linq;
using System;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.SocialPlatforms;
using static OdessaEngine.NETS.Core.NetsNetworking;
using WebSocketSharp;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor;
#endif

namespace OdessaEngine.NETS.Core {
    [ExecuteAlways]
    public class NetsEntity : MonoBehaviour {
        public List<ObjectToSync> ObjectsToSync = new List<ObjectToSync>();
        public Transform addedTransform;

        [Header("Sync properties")]
        [Range(0.0f, 20.0f)]
        public float SyncFramesPerSecond = 1f;
        [SerializeField]
        public string _assignedGuid = new Guid().ToString("N");
        [SerializeField]
        public string assignedGuid {
            get { 
                if(GuidMap.TryGetValue(GetInstanceID(), out var assigned)) {
                    return assigned.ToString("N");
                }
                return _assignedGuid;
            }
            set {
                _assignedGuid = value;
            }
        }

        public enum AuthorityEnum {
            Client,
            Server,
            ServerSingleton,
        }
        public AuthorityEnum Authority;

        public ulong Id;
        public Guid roomGuid;
        NetsEntityState state;
        protected bool destroyedByServer = false;
        public bool hasStarted = false;
        public bool attemptedToCreateOnServer = false;

        private static bool IsNetsNativeType(Type t) => TypedField.SyncableTypeLookup.ContainsKey(t) || new[] { typeof(Vector2), typeof(Vector3), typeof(Quaternion) }.Contains(t);

        private static PropertyInfo[] GetValidPropertiesFor(Type t, bool isTopLevel) => t
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(p => p.GetAccessors().Length == 2)
            .Where(p => !p.GetGetMethod().IsStatic)

            .Where(p => t != typeof(Transform) || new string[] {
                isTopLevel ? nameof(Transform.position) : nameof(Transform.localPosition),
                isTopLevel ? nameof(Transform.rotation) : nameof(Transform.localRotation),
                nameof(Transform.localScale)
            }.Contains(p.Name))

            .Where(p => t != typeof(Rigidbody2D) || new string[] {
                nameof(Rigidbody2D.velocity),
                nameof(Rigidbody2D.angularVelocity),
                nameof(Rigidbody2D.mass),
                //nameof(Rigidbody2D.drag), // linearDrag in webGL :C
                nameof(Rigidbody2D.angularDrag)
            }.Contains(p.Name))

            .Where(p => t != typeof(SpriteRenderer) || new string[] {
                nameof(SpriteRenderer.color),
                nameof(SpriteRenderer.size),
                nameof(SpriteRenderer.flipY),
                nameof(SpriteRenderer.flipX),
            }.Contains(p.Name))

            .Where(p => t != typeof(BoxCollider) || new string[] {
                nameof(BoxCollider.size),
            }.Contains(p.Name))

            .Where(p => t != typeof(BoxCollider2D) || new string[] {
                nameof(BoxCollider2D.size),
            }.Contains(p.Name))

            .Where(p => t != typeof(PolygonCollider2D) || new string[] {
                nameof(PolygonCollider2D.points),
            }.Contains(p.Name))

            .Where(p => t != typeof(AudioSource))

            /* Checking to ensure we had a conversion type for it
             * .Where(p => TypedField.SyncableTypeLookup.ContainsKey(p.PropertyType) || new []{ typeof(Vector2), typeof(Vector3), typeof(Quaternion) }.Contains(p.PropertyType))*/
            .ToArray();
        public enum NetsEntityState {
            Uninitialized,
            Pending,
            Insync
        }
        public void MarkAsDestroyedByServer() {
            destroyedByServer = true;
        }

        public KeyPairEntity localModel;
        KeyPairEntity networkModel;

        public static Dictionary<Guid, NetsEntity> NetsEntityByCreationGuidMap = new Dictionary<Guid, NetsEntity>();
        static Dictionary<string, NetsEntity> NetsEntityByRoomAndIdMap = new Dictionary<string, NetsEntity>();

        private static NETSEntityDefinitions _DefinitionMap = default;
        private static Dictionary<int, Guid> _DefinitionMapped = new Dictionary<int, Guid>();
        static Dictionary<int,Guid> GuidMap { 
            get {
                if(_DefinitionMap == default)
                    LoadOrCreateDefinitionMap();
                return _DefinitionMapped;
            }
        }
        private static bool LoadOrCreateDefinitionMap() {
            _DefinitionMap = Resources.Load("NETSEntityDefinitions") as NETSEntityDefinitions;
#if UNITY_EDITOR
            if (!_DefinitionMap) {
                var scriptable = ScriptableObject.CreateInstance<NETSEntityDefinitions>();
                AssetDatabase.CreateFolder("Assets", "Resources");
                AssetDatabase.CreateAsset(scriptable, "Assets/Resources/NETSEntityDefinitions.asset");
                EditorUtility.SetDirty(scriptable);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                _DefinitionMap = Resources.Load("NETSEntityDefinitions") as NETSEntityDefinitions;
            }
#endif
            _DefinitionMapped = new Dictionary<int, Guid>();
            foreach(var v in _DefinitionMap.GuidMap) {
                _DefinitionMapped.Add(v.instanceID, Guid.ParseExact(v.assignedGuid,"N"));
            }
            return true;
        }
        private static void AddOrUpdateDefinitionMap(NetsEntity obj) {
#if UNITY_EDITOR
            var instanceID = obj.GetInstanceID();
            var parsedValue = Guid.ParseExact(obj.assignedGuid, "N");
            if (_DefinitionMapped.TryGetValue(instanceID, out _)) {
                _DefinitionMapped[instanceID] = parsedValue;
            } else {
                _DefinitionMapped.Add(instanceID, parsedValue);
            }
            _DefinitionMap.GuidMap = NETSEntityDefinitions.GuidMapFromDict(_DefinitionMapped);
            EditorUtility.SetDirty(_DefinitionMap);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
#endif
        }
        private static void RemoveFromDefinitionMap(NetsEntity obj) {
#if UNITY_EDITOR
            var instanceID = obj.GetInstanceID();
            if (_DefinitionMapped.TryGetValue(instanceID, out _)) {
                _DefinitionMapped.Remove(instanceID);
                _DefinitionMap.GuidMap = NETSEntityDefinitions.GuidMapFromDict(_DefinitionMapped);
                EditorUtility.SetDirty(_DefinitionMap);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
#endif
        }

        [HideInInspector]
        public string prefab;

        private void Awake() {
#if UNITY_EDITOR
            if (Application.isPlaying == false) return;
#endif

            localModel = new KeyPairEntity {
                PrefabName = prefab,
                isNew = true,
            };

            //Deal with Assigned GUID in Spawn state
            if (Authority == AuthorityEnum.Client && assignedGuid == new Guid().ToString("N")) assignedGuid = Guid.NewGuid().ToString("N");
            if (Authority == AuthorityEnum.Server && assignedGuid == new Guid().ToString("N")) assignedGuid = Guid.NewGuid().ToString("N");
            if (Authority == AuthorityEnum.ServerSingleton && NetsNetworking.KnownServerSingletons.ContainsKey(prefab) == false) NetsNetworking.KnownServerSingletons.Add(prefab, this);
           
            //Nets entitys don't get destroyed when changing scene
            DontDestroyOnLoad(gameObject);
        }
        private void NetsStart() {
            if (hasStarted) return;
            foreach (var c in transform.GetComponentsInChildren<NetsBehavior>()) {
                c.NetsStart();
                if (OwnedByMe) c.NetsOwnedStart();
            }
            hasStarted = true;
        }

        private void TryCreateOnServer() {
            if (NetsNetworking.instance?.canSend != true) return;
            if (destroyedByServer) return;
            if (state != NetsEntityState.Uninitialized) return;
            if (attemptedToCreateOnServer == false) {
                localModel.SetString(NetsNetworking.AssignedGuidFieldName, assignedGuid);
                NetsEntityByCreationGuidMap.Add(Guid.ParseExact(assignedGuid, "N"), this);
                SetPropertiesBeforeCreation = true;
                LateUpdate();
                SetPropertiesBeforeCreation = false;
                attemptedToCreateOnServer = true;
            }
            if (NetsNetworking.instance?.CreateFromGameObject(this) == true) {
                //print("Asked server to create " + prefab);
                state = NetsEntityState.Pending;
            }
        }

        public void OnCreatedOnServer(Guid roomGuid, KeyPairEntity e) {
            networkModel = e;
            Id = e.Id;
            this.roomGuid = roomGuid;
            NetsEntityByRoomAndIdMap[roomGuid.ToString() + Id] = this;
            var shouldSetFields = OwnedByMe == false;
            if (shouldSetFields)
                e.Fields.ToList().ForEach(kv => OnFieldChange(e, kv.Key, true));
            else {
                if (OwnedByMe == false && Authority == AuthorityEnum.Client) {
                    print("Expected object to have owner as me");
                }
            }
            state = NetsEntityState.Insync;
            NetsStart();
        }

        void Start() {
#if UNITY_EDITOR
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null) {
                PrefabUtility.prefabInstanceUpdated += (a) => {
                    if (a == this)
                        NetsInitialization.OnRuntimeMethodLoad();
                };
                SyncProperties();
            }
            if (Application.isPlaying == false) return;
#endif
            StartCoroutine(createOnServer());
            NetsStart();
        }

        /// <summary>
        /// Use to check if the local account is the owner of this entity
        /// OR 
        /// If this is the server and the server owns this
        /// </summary>
        public bool OwnedByMe => (state != NetsEntityState.Insync && Authority == AuthorityEnum.Client) ||
            (Authority.IsServerOwned() && NetsNetworking.instance?.IsServer == true) ||
            (NetsNetworking.myAccountGuid != null && NetsNetworking.myAccountGuid == networkModel?.Owner);

        /// <summary>
        /// Use to check if the local account was the creator of this entity
        /// </summary>
        public Guid Creator => networkModel?.Creator ?? NetsNetworking.myAccountGuid ?? default;

        public Guid Owner { get { return networkModel?.Owner ?? default; } set { NetsNetworking.instance?.SendOwnershipChange(roomGuid, networkModel.Id, value); } }

        void OnDestroy() {
#if UNITY_EDITOR
            if (Application.isPlaying == false) return;
#endif
            if (OwnedByMe == false && destroyedByServer == false) {
                Debug.LogWarning($"Destroyed entity {prefab} without authority to do so");
                return;
            }
            if (destroyedByServer == false) {
                NetsNetworking.instance?.DestroyEntity(Id);
            }
        }

        public void OnApplicationQuit() {
            destroyedByServer = true;
        }

        public Dictionary<string, ObjectProperty> pathToProperty = new Dictionary<string, ObjectProperty>();
        public Dictionary<string, Vector3LerpingObjectProperty> pathToLerpVector3 = new Dictionary<string, Vector3LerpingObjectProperty>();
        public Dictionary<string, QuaternionLerpingObjectProperty> pathToLerpQuaternion = new Dictionary<string, QuaternionLerpingObjectProperty>();
        HashSet<string> loggedUnknownPaths = new HashSet<string>();
        public ObjectProperty GetPropertyAtPath(string path) {
            if (pathToProperty.TryGetValue(path, out var r)) return r;
            foreach (var t in ObjectsToSync) {
                foreach (var c in t.Components) {
                    foreach (var f in c.Fields) {
                        try {
                            if (f.PathName == path) {
                                var component = t.Transform.GetComponents<Component>().SingleOrDefault(com => com.GetType().Name == c.ClassName);
                                if (component == null) throw new Exception("unknown component for path " + path);
                                var method = GetValidPropertiesFor(component.GetType(), t.IsSelf).SingleOrDefault(prop => prop.Name == f.FieldName);
                                if (method == null) throw new Exception("unknown method for path " + path);
                                var objProp = new ObjectProperty {
                                    Object = component,
                                    Method = method,
                                    Field = f,
                                };
                                pathToProperty[path] = objProp;
                                return objProp;
                            }
                        } catch (Exception e) {
                            if (loggedUnknownPaths.Contains(f.PathName)) return null;
                            Debug.LogError("Unable to get property at path " + path + ". Error: " + e);
                            loggedUnknownPaths.Add(f.PathName);
                        }
                    }
                }
            }
            return null;
        }

        float lastUpdateTime = 0f;
        bool SetPropertiesBeforeCreation = false;
        bool lastOwnershipState = default;
        public void LateUpdate() {
            if (lastOwnershipState == default) {
                if (OwnedByMe)
                    foreach (var c in transform.GetComponentsInChildren<NetsBehavior>())
                        c.NetsOnGainOwnership();
            }
            if (lastOwnershipState != OwnedByMe) {
                foreach (var c in transform.GetComponentsInChildren<NetsBehavior>())
                    if (OwnedByMe)
                        c.NetsOnGainOwnership();
                    else
                        c.NetsOnLostOwnership();
            }
            lastOwnershipState = OwnedByMe;

            if (SetPropertiesBeforeCreation == false) {
                if (state != NetsEntityState.Insync) return;
                if (OwnedByMe == false) return;
                if (Time.time < lastUpdateTime + 1f / SyncFramesPerSecond) return;
            }
            lastUpdateTime = Time.time;

            if (OwnedByMe || SetPropertiesBeforeCreation) {
                foreach (var t in ObjectsToSync) {
                    foreach (var c in t.Components) {
                        foreach (var f in c.Fields) {
                            if (f.Enabled == false) continue;
                            var objProp = GetPropertyAtPath(f.PathName);
                            if (objProp == null) {
                                print("Unable to get property at path: " + f.PathName + " - it's null!");
                                continue;
                            }
                            var objectToSave = objProp.Value();
                            if (IsNetsNativeType(objectToSave.GetType())) {
                                if (objectToSave is Vector2 v2) objectToSave = new System.Numerics.Vector2(v2.x, v2.y);
                                if (objectToSave is Vector3 v3) objectToSave = new System.Numerics.Vector3(v3.x, v3.y, v3.z);
                                if (objectToSave is Quaternion v4) objectToSave = new System.Numerics.Vector4(v4.x, v4.y, v4.z, v4.w);
                                localModel.SetObject(f.PathName, objectToSave);
                            } else {
                                localModel.SetJson(f.PathName, JsonConvert.SerializeObject(objectToSave));
                            }
                        }

                    }

                }
            }

            if (localModel.IsDirty && SetPropertiesBeforeCreation == false) {
                NetsNetworking.instance.WriteEntityDelta(this, localModel);
            }
        }

        public void OnFieldChange(KeyPairEntity entity, string key, bool force = false) {
            if (this == null) return;
            if (OwnedByMe == false || force) {
                if (key.StartsWith(".")) {
                    var objProp = GetPropertyAtPath(key);
                    if (objProp == null) {
                        if (key != NetsNetworking.AssignedGuidFieldName) Debug.Log("Unable to find path: " + key);
                        return;
                    }

                    var obj = entity.GetObject(key);
                    if (obj is System.Numerics.Vector2 v2) obj = v2.ToUnityVector2();
                    if (obj is System.Numerics.Vector3 v3) obj = v3.ToUnityVector3();
                    if (obj is System.Numerics.Vector4 v4) obj = v4.ToUnityQuaternion();
                    if (!IsNetsNativeType(objProp.Method.PropertyType)) {
                        obj = JsonConvert.DeserializeObject((string)obj, objProp.Method.PropertyType);
                    }

                    // Check lerps
                    if (objProp.Field.LerpType != LerpType.None) {
                        if (objProp.Field.FieldType == "Vector3") {
                            if (!pathToLerpVector3.TryGetValue(key, out var lerpObj)) {
                                lerpObj = pathToLerpVector3[key] = new Vector3LerpingObjectProperty {
                                    Field = objProp.Field,
                                    Object = objProp.Object,
                                    Method = objProp.Method,
                                    Lerp = new Vector3AdaptiveLerp(),
                                };
                                lerpObj.Lerp.expectedReceiveDelay = 1 / SyncFramesPerSecond;
                                lerpObj.Lerp.type = objProp.Field.LerpType;
                                lerpObj.SetValue((Vector3)obj);
                            }
                            lerpObj.Lerp.ValueChanged((Vector3)obj);
                            return;
                        } else if (objProp.Field.FieldType == "Quaternion") {
                            if (!pathToLerpQuaternion.TryGetValue(key, out var lerpObj)) {
                                lerpObj = pathToLerpQuaternion[key] = new QuaternionLerpingObjectProperty {
                                    Field = objProp.Field,
                                    Object = objProp.Object,
                                    Method = objProp.Method,
                                    Lerp = new QuaternionAdaptiveLerp(),
                                };
                                lerpObj.Lerp.expectedReceiveDelay = 1 / SyncFramesPerSecond;
                                lerpObj.Lerp.type = objProp.Field.LerpType;
                                lerpObj.SetValue((Quaternion)obj);
                            }
                            lerpObj.Lerp.ValueChanged((Quaternion)obj);
                            return;
                        }

                    }

                    // Else set property directly
                    objProp.SetValue(obj);
                }
            }
        }


        IEnumerator createOnServer() {
            while (true) {
                if (state != NetsEntityState.Insync) {
                    TryCreateOnServer();
                    yield return new WaitForSeconds(1f / 2f);
                    continue;
                } else
                    break;
            }
        }
        public static Guid Int2Guid(int value) {
            byte[] bytes = new byte[16];
            BitConverter.GetBytes(value).CopyTo(bytes, 0);
            return new Guid(bytes);
        }

        bool lastOwnState = false;
        bool ownershipSwitch = false;
        void Update() {
#if UNITY_EDITOR
            if (Application.isPlaying == false) {
                if (GetIsPrefab(gameObject) && assignedGuid == new Guid().ToString("N")) {
                    assignedGuid = Int2Guid(GetInstanceID()).ToString("N");
                    AddOrUpdateDefinitionMap(this);
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                    EditorUtility.SetDirty(gameObject);
                } else if (AmInPrefabIsolationContext(gameObject) && assignedGuid != new Guid().ToString("N")) {
                    assignedGuid = new Guid().ToString("N");
                    RemoveFromDefinitionMap(this);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(this);
                    EditorSceneManager.MarkSceneDirty(gameObject.scene);
                    EditorUtility.SetDirty(gameObject);
                }
            }
#endif
            ownershipSwitch = lastOwnState != OwnedByMe;
            lastOwnState = OwnedByMe;
            if (Application.isPlaying) {
                //if (NetsNetworking.instance == null) return;
                //if (NetsNetworking.instance.canSend == false) return;

                if (ownershipSwitch) {
                    if (OwnedByMe) {//Just switched to server
                        if(networkModel != null)
                            localModel = networkModel.Clone();
                        foreach (var lo in pathToLerpVector3.Values) {
                            lo.SetValue(lo.Lerp.GetMostRecent());
                        }
                        foreach (var lo in pathToLerpQuaternion.Values) {
                            lo.SetValue(lo.Lerp.GetMostRecent());
                        }
                    } else {
                        foreach (var lo in pathToLerpVector3.Values) {
                            lo.Lerp.Reset(1 / SyncFramesPerSecond, (Vector3)lo.Value());
                            lo.Lerp.ValueChanged((Vector3)lo.Value());
                        }
                        foreach (var lo in pathToLerpQuaternion.Values) {
                            lo.Lerp.Reset(1 / SyncFramesPerSecond, (Quaternion)lo.Value());
                            lo.Lerp.ValueChanged((Quaternion)lo.Value());
                        }
                    }
                }

                if (!OwnedByMe && state == NetsEntityState.Insync) {
                    // Run through lerps
                    //if (SyncPosition) GetPositionTransform().position = positionLerp.GetLerped();
                    foreach (var lo in pathToLerpVector3.Values) {
                        lo.SetValue(lo.Lerp.GetLerped());
                    }
                    foreach (var lo in pathToLerpQuaternion.Values) {
                        lo.SetValue(lo.Lerp.GetLerped());
                    }
                }

                foreach (var c in transform.GetComponentsInChildren<NetsBehavior>()) {
                    c.NetsUpdate();
                    if (OwnedByMe)
                        c.NetsOwnedUpdate();
                }
            }
            //Shouldn't need to sync every frame
            SyncProperties();
        }
        private Dictionary<MethodInfo, ulong> methodToIdLookup = new Dictionary<MethodInfo, ulong>();
        private Dictionary<ulong, MethodInfo> idToMethodLookup = new Dictionary<ulong, MethodInfo>();
        ulong methodIndex = 0;
        private void SetUpMethodDict() {
            if (methodToIdLookup.Count > 0)
                return;
            // Get the public methods.
            // We can garuntee that get components will return the same order every time https://answers.unity.com/questions/1293957/reliable-order-of-components-using-getcomponents.html
            foreach (var comp in gameObject.GetComponents<MonoBehaviour>()) {
                if (comp is NetsEntity) continue;
                var type = comp.GetType();
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
                    var index = methodIndex;
                    idToMethodLookup.Add(index, method);
                    methodToIdLookup.Add(method, index);
                    methodIndex++;
                }
            }
        }
        public void SyncProperties() {
#if UNITY_EDITOR
            if (Application.isPlaying) return;
            if (!PrefabUtility.IsPartOfAnyPrefab(gameObject) && PrefabUtility.GetPrefabInstanceStatus(gameObject) == PrefabInstanceStatus.NotAPrefab && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(gameObject)) && PrefabUtility.GetPrefabAssetType(gameObject) == PrefabAssetType.NotAPrefab && string.IsNullOrEmpty(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this)) && PrefabStageUtility.GetCurrentPrefabStage() == null) {
                Debug.LogError($"{gameObject.name} object needs to be a prefab for NetsEntity script to function");
                return;
            }
            if (PrefabUtility.GetPrefabAssetType(gameObject) != PrefabAssetType.NotAPrefab || PrefabStageUtility.GetCurrentPrefabStage() != null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(gameObject)) || !string.IsNullOrEmpty(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this))) {
                Id = 0;
                var longPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this);
                if (string.IsNullOrEmpty(longPath))
                    longPath = PrefabStageUtility.GetCurrentPrefabStage().assetPath;
                if (string.IsNullOrEmpty(longPath))
                    longPath = AssetDatabase.GetAssetPath(gameObject);
                if (string.IsNullOrEmpty(longPath))
                    longPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(this);
                var split = longPath.Split('/');
                var prefabName = split.Last();
                var prefabSplit = prefabName.Split('.');
                var final = prefabSplit.First();
                prefab = final;
                /*
                // First time script/prefab init
                var component = prefab.GetComponent<NetsEntity>();
                if (component == null) {
                    component = prefab.AddComponent<NetsEntity>();
                    var go = gameObject;
                    DestroyImmediate(this);
                    Selection.activeObject = prefab;
                }
                component.prefab = prefab.name;
                */
                // Fill in Objects to sync
                if (ObjectsToSync.Any(o => o.Transform == transform) == false)
                    ObjectsToSync.Insert(0, new ObjectToSync {
                        Transform = transform,
                        Components = new List<ComponentsToSync>(),
                    });

                ObjectsToSync.ForEach(o => o.IsSelf = false);
                ObjectsToSync[0].IsSelf = true;

                foreach (var obj in ObjectsToSync) {
                    var components = obj.Transform.GetComponents<Component>();

                    foreach (var comp in components) {
                        if (comp is NetsEntity) continue;

                        var componentToSync = obj.Components.FirstOrDefault(f => f.ClassName == comp.GetType().Name);
                        if (componentToSync == null) {
                            componentToSync = new ComponentsToSync {
                                ClassName = comp.GetType().Name,
                                Fields = new List<ScriptFieldToSync>(),
                            };
                            obj.Components.Add(componentToSync);
                        }

                        var componentFields = new List<ScriptFieldToSync>();
                        var props = GetValidPropertiesFor(comp.GetType(), obj.IsSelf);

                        foreach (var p in props) {
                            var propToSync = componentToSync.Fields.FirstOrDefault(f => f.FieldName == p.Name);
                            if (propToSync == null) {
                                propToSync = new ScriptFieldToSync {
                                    FieldName = p.Name,
                                    PathName = "." + (obj.IsSelf ? this.prefab : obj.Transform.name) + "." + comp.GetType().Name + "." + p.Name,
                                    Enabled = true,
                                    LerpType = LerpType.Velocity,
                                };
                                componentToSync.Fields.Add(propToSync);
                            }
                            propToSync.FieldType = p.PropertyType.Name;
                            propToSync.PathName = "." + (obj.IsSelf ? this.prefab : obj.Transform.name) + "." + comp.GetType().Name + "." + p.Name;
                        }
                        componentToSync.Fields = componentToSync.Fields.Where(f => props.Any(p => p.Name == f.FieldName)).ToList();
                    }
                    obj.Components = obj.Components.Where(f => components.Any(c => c.GetType().Name == f.ClassName)).ToList();
                    EditorUtility.SetDirty(gameObject);
                }
            }


#endif
        }
        public void InterpretMethod(string MethodEvent) {
            if (destroyedByServer) return;//Don't run on ents that are flagged as dead
            SetUpMethodDict();
            var e = JsonConvert.DeserializeObject<MethodEvent>(MethodEvent);
            if (idToMethodLookup.TryGetValue(e.methodId, out var method)) {
                ParameterInfo[] _params = method.GetParameters();
                var typedParams = _params.Select((p, i) => {
                    var obj = e.args[i];
                    if (obj is JObject j)
                        return j.ToObject(p.ParameterType);
                    return Convert.ChangeType(obj, p.ParameterType);
                }).ToList();
                Component comp = GetComponent(method.DeclaringType);
                if (comp)
                    method.Invoke(comp, typedParams.ToArray());
                else
                    Debug.Log($"Component doesn't exist {comp.name}");
            } else
                throw new Exception($"Received RPC that we don't have a method for {e.methodId}");
        }
        protected class MethodEvent {
            public object[] args;
            public ulong methodId;
        }
        private void RPC(MethodInfo method, object[] args) {
            //Set dependencies
            SetUpMethodDict();
            if (OwnedByMe) {
                method.Invoke(GetComponent(method.DeclaringType), args);
                return;
            }
            if (methodToIdLookup.TryGetValue(method, out var index)) {
                if (Id == 0) {
                    TryCreateOnServer();
                    if (assignedGuid == default)
                        throw new Exception($"No creation Guid for NETS Entity Name: {method.DeclaringType.Name}.{method.Name}");
                    try {
                        NetsNetworking.instance.SendEntityEventByCreationGuid(roomGuid, Guid.ParseExact(assignedGuid,"N"), JsonConvert.SerializeObject(new MethodEvent { methodId = index, args = args }));
                    } catch (Exception e) {
                        Debug.LogError(e);
                    }
                } else {
                    NetsNetworking.instance.SendEntityEvent(roomGuid, Id, JsonConvert.SerializeObject(new MethodEvent { methodId = index, args = args }));
                }
            } else
                Debug.LogError($"No method matching  {method.DeclaringType.Name}.{method.Name}");
        }
        public void RPC(Action method) => RPC(method.Method, new object[] { });
        public void RPC<T1>(Action<T1> method, T1 arg1) => RPC(method.Method, new object[] { arg1 });
        public void RPC<T1, T2>(Action<T1, T2> method, T1 arg1, T2 arg2) => RPC(method.Method, new object[] { arg1, arg2 });
        public void RPC<T1, T2, T3>(Action<T1, T2, T3> method, T1 arg1, T2 arg2, T3 arg3) => RPC(method.Method, new object[] { arg1, arg2, arg3 });
        public void RPC<T1, T2, T3, T4>(Action<T1, T2, T3, T4> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4 });
        public void RPC<T1, T2, T3, T4, T5>(Action<T1, T2, T3, T4, T5> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5 });
        public void RPC<T1, T2, T3, T4, T5, T6>(Action<T1, T2, T3, T4, T5, T6> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5, arg6 });
        public void RPC<T1, T2, T3, T4, T5, T6, T7>(Action<T1, T2, T3, T4, T5, T6, T7> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7 });
        public void RPC<T1, T2, T3, T4, T5, T6, T7, T8>(Action<T1, T2, T3, T4, T5, T6, T7, T8> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8 });
        public void RPC<T1, T2, T3, T4, T5, T6, T7, T8, T9>(Action<T1, T2, T3, T4, T5, T6, T7, T8, T9> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9 });
        public void RPC<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10>(Action<T1, T2, T3, T4, T5, T6, T7, T8, T9, T10> method, T1 arg1, T2 arg2, T3 arg3, T4 arg4, T5 arg5, T6 arg6, T7 arg7, T8 arg8, T9 arg9, T10 arg10) => RPC(method.Method, new object[] { arg1, arg2, arg3, arg4, arg5, arg6, arg7, arg8, arg9, arg10 });
        public void RPC(Action method, object[] parameters) => RPC(method.Method, parameters);

        public static bool GetIsPrefab(GameObject obj) {
#if UNITY_EDITOR
            return !(PrefabUtility.GetPrefabAssetType(obj) == PrefabAssetType.MissingAsset || PrefabUtility.GetPrefabAssetType(obj) == PrefabAssetType.NotAPrefab);
#endif
            return false;
        }
        public static bool IsInPrefabMode(GameObject obj) {
#if UNITY_EDITOR
            return PrefabStageUtility.GetCurrentPrefabStage()?.scene == SceneManager.GetActiveScene();
#endif
            return false;
        }
        public static bool AmInPrefabInstanceContext(GameObject obj) {
#if UNITY_EDITOR
            var mode = PrefabStageUtility.GetPrefabStage(obj)?.mode;
            return mode == PrefabStage.Mode.InContext;
#endif
            return false;
        }
        public static bool AmInPrefabIsolationContext(GameObject obj) {
#if UNITY_EDITOR
            return PrefabStageUtility.GetPrefabStage(obj)?.mode == PrefabStage.Mode.InIsolation;
#endif
            return false;
        }
    }

   [Serializable]
    public class ObjectToSync {
        public Transform Transform;
        public List<ComponentsToSync> Components;
        public bool IsSelf;
    }

    [Serializable]
    public class ComponentsToSync {
        public string ClassName;
        public List<ScriptFieldToSync> Fields;
    }

    [Serializable]
    public class ScriptFieldToSync {
        public string FieldName;
        public string PathName;
        public bool Enabled;
        public string FieldType;
        public LerpType LerpType = LerpType.None;
    }

    public class ObjectProperty {
        public object Object { get; set; }
        public PropertyInfo Method { get; set; }
        public ScriptFieldToSync Field { get; set; }
        public object Value() => Method.GetValue(Object);
        public void SetValue(object value) => Method.SetValue(Object, value);
    }

    public class Vector3LerpingObjectProperty : ObjectProperty {
        public Vector3AdaptiveLerp Lerp { get; set; }
    }
    public class QuaternionLerpingObjectProperty : ObjectProperty {
        public QuaternionAdaptiveLerp Lerp { get; set; }
    }
}