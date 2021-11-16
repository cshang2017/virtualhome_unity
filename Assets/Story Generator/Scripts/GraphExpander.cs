﻿using StoryGenerator.CharInteraction;
using StoryGenerator.DoorProperties;
using StoryGenerator.HomeAnnotation;
using StoryGenerator.Recording;
using StoryGenerator.RoomProperties;
using StoryGenerator.Scripts;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

namespace StoryGenerator.Utilities
{

    class SceneExpanderResult
    {
        private Dictionary<string, HashSet<object>> messages = new Dictionary<string, HashSet<object>>();

        public List<IEnumerator> enumerators = new List<IEnumerator>();

        public void AddItem(string key, object item)
        {
            HashSet<object> current;

            if (!messages.TryGetValue(key, out current)) {
                current = new HashSet<object>();
                messages[key] = current;
            }
            current.Add(item);
        }

        public Dictionary<string, HashSet<object>> Messages { get { return messages; } }

        public bool Success
        {
            get { return messages.Values.Count(l => l.Count > 0) == 0; }
        }
    }

    class ExpanderException : Exception
    {
        public ExpanderException(bool fatal, string message) : 
            base(message)
        {
        }
    }

    class GraphObjectAlignment
    {
        private readonly NameEquivalenceProvider nameEquivalenceProvider;

        public GraphObjectAlignment(NameEquivalenceProvider nameEquivalenceProvider)
        {
            this.nameEquivalenceProvider = nameEquivalenceProvider;
        }

        public double GapPenalty { get; set; } = -1.0;
        public double SimilarityPenalty { get; } = -1000.0;

        public IDictionary<int, EnvironmentObject> Align(EnvironmentGraph graphA, EnvironmentGraph graphB, bool exact_alignment)
        {
            IDictionary<int, EnvironmentObject> result = new Dictionary<int, EnvironmentObject>();
            IList<EnvironmentObject> nodesA = graphA.nodes;
            IList<EnvironmentObject> nodesB = graphB.nodes;

            if (exact_alignment)
            {
                IDictionary<int, EnvironmentObject> nodesBMap = new Dictionary<int, EnvironmentObject>();
                foreach (EnvironmentObject nodeB in nodesB)
                {
                    nodesBMap[nodeB.id] = nodeB;
                }
                foreach (EnvironmentObject nodeA in nodesA)
                {
                    EnvironmentObject objB;
                    if (nodesBMap.TryGetValue(nodeA.id, out objB))
                    {
                        result[nodeA.id] = objB;
                    }
                }
            }
            else
            {
                double[,] F = CalcFMatrix(nodesA, nodesB);
                int i = nodesA.Count;
                int j = nodesB.Count;

                while (i > 0 || j > 0)
                {
                    if (i > 0 && j > 0 && F[i, j] == F[i - 1, j - 1] + NodeSimilarity(nodesA[i - 1], nodesB[j - 1]))
                    {
                        result[nodesA[i - 1].id] = nodesB[j - 1];
                        i--;
                        j--;
                    }
                    else if (i > 0 && F[i, j] == F[i - 1, j] + GapPenalty)
                    {
                        i--;
                    }
                    else
                    {
                        j--;
                    }
                }
                
                
            }
            return result;
        }

        private double[,] CalcFMatrix(IList<EnvironmentObject> nodesA, IList<EnvironmentObject> nodesB)
        {
            double[,] F = new double[nodesA.Count + 1, nodesB.Count + 1];

            for (int i = 0; i <= nodesA.Count; i++)
                F[i, 0] = GapPenalty * i;
            for (int j = 1; j <= nodesB.Count; j++)
                F[0, j] = GapPenalty * j;
            for (int i = 1; i <= nodesA.Count; i++) {
                for (int j = 1; j <= nodesB.Count; j++) {
                    double match = F[i - 1, j - 1] + NodeSimilarity(nodesA[i - 1], nodesB[j - 1]);
                    double delete = F[i - 1, j] + GapPenalty;
                    double insert = F[i, j - 1] + GapPenalty;
                    F[i, j] = Math.Max(match, Math.Max(delete, insert));
                }
            }
            return F;
        }

        private double NodeSimilarity(EnvironmentObject oA, EnvironmentObject oB)
        {
            return nameEquivalenceProvider.IsEquivalent(oB.class_name, oA.class_name) ? 0.0 : SimilarityPenalty;
        }

    }


    class SceneExpander
    {
        private const string UNALIGNED_IDS = "unaligned_ids";
        private const string MISSING_DEST = "missing_destinations";
        private const string MISSING_INT = "missing_interactions";
        private const string UNPLACED = "unplaced";
        private const string MISSING_PREF = "missing_prefabs";
        private const string FATAL = "fatal_error";

        private const int NEW_OBJECT_ID = 1000;

        private DataProviders dataProviders;
        public bool Randomize { get; set; }
        public bool IgnoreObstacles { get; set; }
        public bool AnimateCharacter { get; set; }
        public bool TransferTransform { get; set; }

        public IDictionary<string, List<string>> AssetsMap { get; set; }

        // Initialized in ExpandScene. Should be used only in calls from that method
        private Dictionary<int, EnvironmentObject> id2ObjectMap;
        private Dictionary<int, EnvironmentObject> sceneId2ObjectMap;
        IDictionary<int, EnvironmentObject> alignment;
        private Dictionary<Tuple<int, ObjectRelation>, List<EnvironmentObject>> edgeMap;
        private Dictionary<Tuple<int, ObjectRelation>, List<EnvironmentObject>> sceneEdgeMap;
        private Dictionary<int, List<EnvironmentObject>> roomObjectsMap;
        private SceneExpanderResult expanderResult = new SceneExpanderResult();


        public SceneExpander(DataProviders dataProviders)
        {
            this.dataProviders = dataProviders;
        }

        public SceneExpanderResult GetResult()
        {
            return expanderResult;
        }

        public void ExpandScene(Transform transform, EnvironmentGraph graph, EnvironmentGraph sceneGraph, int previousExpandsCount, bool exact_alignment = false)
        {
            CreateGraphMaps(graph, out id2ObjectMap, out edgeMap);
            CreateGraphMaps(sceneGraph, out sceneId2ObjectMap, out sceneEdgeMap);
            var tm = TimeMeasurement.Start("alignment");
            alignment = new GraphObjectAlignment(dataProviders.NameEquivalenceProvider).Align(graph, sceneGraph, exact_alignment);
            TimeMeasurement.Stop(tm);
            Debug.Log("alignment took: " + TimeMeasurement.PrintResults());
            roomObjectsMap = CreateRoomObjectsMap(graph);
            expanderResult = new SceneExpanderResult();

            try {
                DoExpandScene(transform, graph, sceneGraph, exact_alignment);
            } catch (ExpanderException e) {
                expanderResult.AddItem(FATAL, e.Message);
            }
        }

        private void DoExpandScene(Transform transform, EnvironmentGraph graph, EnvironmentGraph sceneGraph, bool exact_alignment=false)
        {
            // Detect missing objects and invalid class ids
            List<EnvironmentObject> missingObjects = new List<EnvironmentObject>();

            EnvironmentObject characterObject = null;
            EnvironmentObject sceneCharacterObject = null;

            // For which objects we found a maching transformation
            List<int> id_transformed = new List<int>();

            foreach (EnvironmentObject obj in graph.nodes) {
                if (obj.class_name == "character")
                    characterObject = obj;

                EnvironmentObject sceneObj;

                if (!alignment.TryGetValue(obj.id, out sceneObj)) {
                    missingObjects.Add(obj);
                    // objectMessages.Add($"Missing {obj.class_name} ({obj.id})");
                } else {
                    if (sceneObj.class_name == "character") {
                        sceneCharacterObject = sceneObj;
                    }
                    
                    if (this.TransferTransform && obj.obj_transform != null && obj.prefab_name == sceneObj.prefab_name)
                    {
                        sceneObj.transform.position = obj.obj_transform.GetPosition();
                        sceneObj.transform.rotation = obj.obj_transform.GetRotation();
                        if (sceneObj.category == "Rooms")
                        {
                            sceneObj.bounding_box = new ObjectBounds(sceneObj.transform.gameObject.GetComponent<RoomProperties.Properties_room>().bounds); 
                        }
                        else
                        {
                            sceneObj.bounding_box = ObjectBounds.FromGameObject(sceneObj.transform.gameObject);
                        }
                        id_transformed.Add(obj.id);
                    }

                    obj.transform = sceneObj.transform;
                    obj.prefab_name = sceneObj.prefab_name;
                    obj.bounding_box = sceneObj.bounding_box;
                }
            }

            // Report unaligned objects (ids) from graph
            if (!exact_alignment)
            {
                foreach (var e in id2ObjectMap)
                {
                    if (!alignment.ContainsKey(e.Key) && e.Key < NEW_OBJECT_ID)
                    {
                        expanderResult.AddItem(UNALIGNED_IDS, e.Key);
                    }
                }
            }

            // Remove unaligned objects from sceneGraph
            List<int> alignedSceneObjects = alignment.Values.Select(o => o.id).ToList();
            HashSet<int> idsToRemove = new HashSet<int>(sceneId2ObjectMap.Keys);
            idsToRemove.ExceptWith(alignedSceneObjects);

            foreach (int id in idsToRemove) {
                try
                {
                    sceneId2ObjectMap[id].transform.gameObject.SetActive(false);
                }
                catch
                {
                    EnvironmentObject aux = sceneId2ObjectMap[id];
                    Debug.Log("ERROR");
                }
                
            }

            // Add missing objects
            foreach (EnvironmentObject obj in missingObjects) {
                // Check if object transformation is available

                bool object_inst = false;
                GameObject loadedObj = null;
                if (this.TransferTransform)
                {
                    if (obj.prefab_name != null)
                    {
                        loadedObj = Resources.Load(ScriptUtils.TransformResourceFileName(obj.prefab_name)) as GameObject;
                    }
                    if (loadedObj == null)
                    {
                        List<string> names = dataProviders.NameEquivalenceProvider.GetEquivalentNames(obj.class_name);
                        if (names.Count > 0)
                        {
                            List<string> fileNames;
                            if (TryGetAssets(names[0], out fileNames))
                            {
                                if (obj.obj_transform != null || obj.bounding_box != null)
                                {
                                    loadedObj = Resources.Load(ScriptUtils.TransformResourceFileName(fileNames[0])) as GameObject;
                                }
                                
                            }
                        }

                    }
                }
                
                if (loadedObj != null)
                {

                    List<EnvironmentObject> objectsInRelation;
                    if (edgeMap.TryGetValue(Tuple.Create(obj.id, ObjectRelation.INSIDE), out objectsInRelation))
                    {
                        if (loadedObj != null && GameObjectUtils.GetCollider(loadedObj) != null)
                        {
                            Transform destObjRoom = GameObjectUtils.GetRoomTransform(objectsInRelation[0].transform);
                            GameObject newGo = UnityEngine.Object.Instantiate(loadedObj, destObjRoom) as GameObject;
                            newGo.name = loadedObj.name;
                            if (obj.obj_transform == null)
                            {
                                if (obj.bounding_box == null)
                                {
                                    // We should never be getting here...
                                    object_inst = false;
                                    UnityEngine.Object.Destroy(loadedObj);

                                }
                                    
                                else
                                {
                                    // Set position based on bounding box
                                    ObjectBounds lo = ObjectBounds.FromGameObject(newGo);
                                    Vector3 loc = new Vector3(lo.center[0], lo.center[1], lo.center[2]);
                                    Vector3 objc = new Vector3(obj.bounding_box.center[0], obj.bounding_box.center[1], obj.bounding_box.center[2]);
                                    newGo.transform.position = newGo.transform.position - loc + objc;
                                    object_inst = true;
                                    obj.transform = newGo.transform;
                                    ColorEncoding.EncodeGameObject(newGo);
                                }

                            }
                            else
                            {
                                Vector3 position = obj.obj_transform.GetPosition();
                                Quaternion rotation = obj.obj_transform.GetRotation();
                                newGo.transform.position = position;
                                newGo.transform.rotation = rotation;
                                object_inst = true;

                                ColorEncoding.EncodeGameObject(newGo);
                            }

                        }
                    }
                }
            


                if (!object_inst)
                {
                    // Put inside

                    if (HandleCreateInside(obj))
                        continue;

                    // Put on
                    if (HandleCreateOn(obj))
                        continue;

                    // Put inside
                    if (!edgeMap.ContainsKey(Tuple.Create(obj.id, ObjectRelation.ON)))
                    {
                        if (HandleCreateInsideRoom(obj))
                            continue;
                    }
                    expanderResult.AddItem(UNPLACED, obj.class_name + "." + obj.id.ToString());
                }

            }

            List<EnvironmentObject> heldObjsLH = GetObjectsInRelation(edgeMap, characterObject, ObjectRelation.HOLDS_LH);
            List<EnvironmentObject> heldSceneObjsLH = GetObjectsInRelation(sceneEdgeMap, sceneCharacterObject, ObjectRelation.HOLDS_LH);
            List<EnvironmentObject> heldObjsRH = GetObjectsInRelation(edgeMap, characterObject, ObjectRelation.HOLDS_RH);
            List<EnvironmentObject> heldSceneObjsRH = GetObjectsInRelation(sceneEdgeMap, sceneCharacterObject, ObjectRelation.HOLDS_RH);

            // Change relations -- move objects
            foreach (EnvironmentObject obj in graph.nodes) {
                // If we don't have a model, check if we interact with it
                if (obj.transform == null) {

                    // Report error if holding of obj has changed 
                    if (heldObjsRH.Contains(obj) || heldObjsLH.Contains(obj)) {
                        expanderResult.AddItem(MISSING_INT, obj.class_name + "." + obj.id.ToString());
                        continue;
                    }
                }

                if (missingObjects.Contains(obj)) {
                    continue;
                }

                List<EnvironmentObject> objsInRelation;
                List<EnvironmentObject> sceneObjsInRelation;

                bool object_inst = false;
                if (this.TransferTransform)
                {
                    if (id_transformed.Contains(obj.id))
                    {
                        object_inst = true;
                        List<EnvironmentObject> objectsInRelation;
                        if (edgeMap.TryGetValue(Tuple.Create(obj.id, ObjectRelation.INSIDE), out objectsInRelation))
                        {
                            Transform destObjRoom = GameObjectUtils.GetRoomTransform(objectsInRelation[0].transform);
                            obj.transform.SetParent(destObjRoom);


                        }
                    }
                }

                if (!object_inst)
                {

                    // "ON"
                    objsInRelation = GetObjectsInRelation(edgeMap, obj, ObjectRelation.ON);
                    sceneObjsInRelation = GetObjectsInRelation(sceneEdgeMap, obj, ObjectRelation.ON);

                    if (HandleOnPlacing(obj, objsInRelation, sceneObjsInRelation))
                        continue;

                    if (characterObject != null)
                    {
                        // "HOLDS_LH"
                        if (HandleHoldsPlacing(ObjectRelation.HOLDS_LH, characterObject, obj, heldObjsLH, heldSceneObjsLH))
                            continue;

                        // "HOLDS_RH"
                        if (HandleHoldsPlacing(ObjectRelation.HOLDS_RH, characterObject, obj, heldObjsRH, heldSceneObjsRH))
                            continue;
                    }

                    // "INSIDE"
                    objsInRelation = GetObjectsInRelation(edgeMap, obj, ObjectRelation.INSIDE);
                    sceneObjsInRelation = GetObjectsInRelation(sceneEdgeMap, obj, ObjectRelation.INSIDE);

                    if (characterObject != null && obj.Equals(characterObject))
                    {
                        HandleCharacterInsidePlacing(obj, objsInRelation, sceneObjsInRelation, GetObjectsInRelation(edgeMap, obj, ObjectRelation.CLOSE),
                            GetObjectsInRelation(sceneEdgeMap, obj, ObjectRelation.CLOSE));
                    } else {
                 
                        HandleInsidePlacing(obj, objsInRelation, sceneObjsInRelation);
                    }
                }
            }
            
            // Change states
            foreach (EnvironmentObject obj in graph.nodes) {
                if (!alignment.ContainsKey(obj.id)) {
                    continue;
                }

                if (obj.transform == null) {
                    if (!obj.states_set.SetEquals(alignment[obj.id].states_set)) {
                        expanderResult.AddItem(MISSING_INT, obj.class_name + "." + obj.id.ToString());
                    }
                    continue;
                }
                ApplyState(obj, alignment[obj.id].states_set);
            }

        }

        private Dictionary<int, List<EnvironmentObject>> CreateRoomObjectsMap(EnvironmentGraph graph)
        {
            Dictionary<int, List<EnvironmentObject>> result = new Dictionary<int, List<EnvironmentObject>>();

            foreach (EnvironmentObject obj in graph.nodes) {
                if (obj.category == EnvironmentGraphCreator.RoomsCategory)
                    result[obj.id] = new List<EnvironmentObject>();
            }
            foreach (EnvironmentRelation e in graph.edges) {
                if (e.relation == ObjectRelation.INSIDE && result.ContainsKey(e.to_id)) {
                    result[e.to_id].Add(id2ObjectMap[e.from_id]);
                }
            }
            return result;
        }

        private bool HandleCreateOn(EnvironmentObject obj)
        {
            List<EnvironmentObject> objectsInRelation;

            if (edgeMap.TryGetValue(Tuple.Create(obj.id, ObjectRelation.ON), out objectsInRelation)) {
                foreach (EnvironmentObject obj2 in objectsInRelation) {
                    EnvironmentObject sceneObj2;

                    if (alignment.TryGetValue(obj2.id, out sceneObj2)) {
                        if (TryPlaceObject(obj, sceneObj2, false)) {
                            return true;
                        }
                    } else {
                        expanderResult.AddItem(MISSING_DEST, obj2.class_name + "." + obj2.id.ToString());
                        // Debug.Log($"Cannot find {obj2.class_name} to put {obj.class_name} on it");
                    }
                }
            }
            return false;
        }

        private bool HandleCreateInside(EnvironmentObject obj)
        {
            List<EnvironmentObject> objectsInRelation;

            if (edgeMap.TryGetValue(Tuple.Create(obj.id, ObjectRelation.INSIDE), out objectsInRelation)) {
                foreach (EnvironmentObject obj2 in objectsInRelation) {
                    if (obj2.category != EnvironmentGraphCreator.RoomsCategory) {
                        EnvironmentObject sceneObj2;
                        if (alignment.TryGetValue(obj2.id, out sceneObj2)) {
                            if (TryPlaceObject(obj, sceneObj2, true)) {
                                return true;
                            }
                        } else {
                            expanderResult.AddItem(MISSING_DEST, obj2.class_name + "." + obj2.id.ToString());
                            // Debug.Log($"Cannot find {obj2.class_name} to put {obj.class_name} inside it");
                        }
                    }
                }
            }
            return false;
        }

        private bool HandleCreateInsideRoom(EnvironmentObject obj)
        {
            List<EnvironmentObject> objectsInRelation;

            if (edgeMap.TryGetValue(Tuple.Create(obj.id, ObjectRelation.INSIDE), out objectsInRelation)) {
                foreach (EnvironmentObject obj2 in objectsInRelation) {
                    if (obj2.category == EnvironmentGraphCreator.RoomsCategory) {
                        Vector3 roomCenter = obj2.transform.GetComponent<Properties_room>().bounds.center;

                        foreach (EnvironmentObject roomObj in roomObjectsMap[obj2.id]) {
                            if (roomObj.transform == null)
                                continue;

                            if (roomObj.properties.Contains("SURFACES") && Vector3.Distance(roomObj.transform.position, roomCenter) < 2.0f) {
                                EnvironmentObject sceneObj2;

                                if (alignment.TryGetValue(roomObj.id, out sceneObj2)) {
                                    if (TryPlaceObject(obj, sceneObj2, false)) {
                                        return true;
                                    }
                                }
                            }
                        }
                        break;
                    }
                }
            }
            return false;
        }

        private void HandleInsidePlacing(EnvironmentObject obj, List<EnvironmentObject> objsInRelation, List<EnvironmentObject> sceneObjsInRelation)
        {
            objsInRelation.RemoveAll(o => o.category == "Rooms");
            sceneObjsInRelation.RemoveAll(o => o.category == "Rooms");

            IEnumerable<EnvironmentObject> intersection = objsInRelation.Intersect(sceneObjsInRelation);

            if (intersection.Count() > 0 || objsInRelation.Count == 0)
                return;

            if (!MoveObject(true, obj, objsInRelation[0]))
            {
                new ExpanderException(true, "Object " + obj.class_name + " cannot be placed");
                // TODO: what happens if no place?
            }
        }

        private void HandleCharacterInsidePlacing(EnvironmentObject obj, List<EnvironmentObject> objsInRelation, List<EnvironmentObject> sceneObjsInRelation,
            List<EnvironmentObject> objsClose, List<EnvironmentObject> sceneObjsClose)
        {
            if (obj.states_set.Contains(ObjectState.SITTING))
                return;

            EnvironmentObject roomObj = objsInRelation.Find(o => o.category == "Rooms");
            EnvironmentObject sceneRoomObj = sceneObjsInRelation.Find(o => o.category == "Rooms");

            if (roomObj == null || sceneRoomObj == null)
                return;

            if (roomObj.Equals(sceneRoomObj) && new HashSet<EnvironmentObject>(objsClose).SetEquals(sceneObjsClose))
                return;

            Bounds roomBounds = roomObj.transform.GetComponent<Properties_room>().bounds;
            Vector3 center;

            if (objsClose.Count == 0) {
                center = roomBounds.center;
            } else {
                center = Vector3.zero;
                int count = 0;

                foreach (EnvironmentObject o in objsClose) {
                    if (o.transform != null && o.category != EnvironmentGraphCreator.RoomsCategory) {
                        center += o.transform.position;
                        count++;
                    }
                }
                if (count > 0) {
                    center /= count;
                } else {
                    center = roomBounds.center;
                }
            }

            List<Vector3> positions = GameObjectUtils.CalculateDestinationPositions(center, obj.transform.gameObject, roomBounds);

            if (positions.Count > 0) {
                if (AnimateCharacter) {
                    CharacterControl cc = obj.transform.GetComponent<CharacterControl>();
                    expanderResult.enumerators.Add(AnimatedGotoEnumerator(cc, positions));
                } else {
                    NavMeshAgent nma = obj.transform.GetComponent<NavMeshAgent>();
                    nma.Warp(positions[0]);
                }
            }
        }

        // Returns true if there is already requested "on" relation or obj was placed on the first one in objsInRelation
        private bool HandleOnPlacing(EnvironmentObject obj, List<EnvironmentObject> objsInRelation, List<EnvironmentObject> sceneObjsInRelation)
        {
            if (obj.class_name == EnvironmentGraphCreator.CharacterClassName)
                return false;

            IEnumerable<EnvironmentObject> intersection = objsInRelation.Intersect(sceneObjsInRelation);

            if (intersection.Count() > 0)
                return true;

            if (objsInRelation.Count == 0)
                return false;

            MoveObject(false, obj, objsInRelation[0]); // TODO: what happens if no place?
            return true;
        }

        // Returns true if there is already requested relation or obj was placed in the hand
        private bool HandleHoldsPlacing(ObjectRelation rel, EnvironmentObject characterObject, EnvironmentObject obj, List<EnvironmentObject> objsInRelation, List<EnvironmentObject> sceneObjsInRelation)
        {
            if (objsInRelation.Contains(obj) && sceneObjsInRelation.Contains(obj))
                return true;

            if (objsInRelation.Contains(obj)) {
                string handName = rel == ObjectRelation.HOLDS_LH ? "MiddleFinger1_L" : "MiddleFinger1_R";
                var hands = ScriptUtils.FindAllObjects(characterObject.transform, t => t.name == handName);
                obj.transform.parent = hands.First().transform;
                obj.transform.localPosition = Vector3.zero;
                return true;
            }
            return false;
        }

        private void CreateGraphMaps(EnvironmentGraph graph, out Dictionary<int, EnvironmentObject> id2ObjectMap,
            out Dictionary<Tuple<int, ObjectRelation>, List<EnvironmentObject>> edgeMap)
        {
            id2ObjectMap = graph.nodes.ToDictionary(n => n.id, n => n);
            edgeMap = new Dictionary<Tuple<int, ObjectRelation>, List<EnvironmentObject>>();

            // Initialize edgeMap
            foreach (EnvironmentRelation rel in graph.edges) {
                List<EnvironmentObject> objectsTo;

                if (!edgeMap.TryGetValue(Tuple.Create(rel.from_id, rel.relation), out objectsTo)) {
                    objectsTo = new List<EnvironmentObject>();
                    edgeMap[Tuple.Create(rel.from_id, rel.relation)] = objectsTo;
                }
                objectsTo.Add(id2ObjectMap[rel.to_id]);
            }
        }

        private void ApplyState(EnvironmentObject obj, ICollection<ObjectState> sceneStates)
        {
            GameObject go = obj.transform.gameObject;
            Properties_door pd = go.GetComponent<Properties_door>();

            if (pd != null) {
                // Doors (between rooms). Open by default, act only if states explicitly contains CLOSED
                if (obj.states_set.Contains(ObjectState.OPEN) && sceneStates.Contains(ObjectState.CLOSED)) {
                    pd.SetDoorToOpen();
                } else if (obj.states_set.Contains(ObjectState.CLOSED) && sceneStates.Contains(ObjectState.OPEN)) {
                    pd.SetDoorToClose();
                }
                // Return, doors can't have other states
                return;
            }

            HandInteraction hi = go.GetComponent<HandInteraction>();

            if (hi != null && hi.switches != null) {

                // Fridge, cabinet, ... doors, closed by default, act only if states expilictly contains OPEN
                // Make this conditional if some class/instance or has other default state
                // ObjectState oppositeInitialOpenableState = ObjectState.OPEN;

                if (obj.states_set.Contains(ObjectState.OPEN) && sceneStates.Contains(ObjectState.CLOSED) ||
                        obj.states_set.Contains(ObjectState.CLOSED) && sceneStates.Contains(ObjectState.OPEN))
                {
                    foreach (HandInteraction.ActivationSwitch asw in hi.switches)
                    {
                        if (asw.action == HandInteraction.ActivationAction.Open)
                        {
                            if (asw.transitionSequences?.Count > 0 && asw.transitionSequences[0].transitions?.Count > 0)
                            {
                                //
                                //asw.transitionSequences
                                //asw.UpdateStateObject();
                                if (obj.states_set.Contains(ObjectState.OPEN))
                                {
                                    alignment[obj.id].states_set.Remove(Utilities.ObjectState.CLOSED);
                                    alignment[obj.id].states_set.Add(Utilities.ObjectState.OPEN);
                                    asw.transitionSequences[0].transitions[0].Flip();
                                }

                                else
                                {
                                    alignment[obj.id].states_set.Remove(Utilities.ObjectState.OPEN);
                                    alignment[obj.id].states_set.Add(Utilities.ObjectState.CLOSED);
                                    asw.transitionSequences[0].transitions[1].Flip();
                                }

                                break;
                            }
                        }
                    }
                }

                // All switchable objects are off by default, except of lights
                // ObjectState opositeInitialSwitchedState = EnvironmentGraphCreator.DEFAULT_ON_OBJECTS.Contains(obj.class_name) ? ObjectState.OFF : ObjectState.ON;

                if (obj.states_set.Contains(ObjectState.ON) && sceneStates.Contains(ObjectState.OFF) ||
                        obj.states_set.Contains(ObjectState.OFF) && sceneStates.Contains(ObjectState.ON))
                {

                    if (obj.states_set.Contains(ObjectState.OFF))
                    {

                        alignment[obj.id].states_set.Remove(Utilities.ObjectState.ON);
                        alignment[obj.id].states_set.Add(Utilities.ObjectState.OFF);
                    }
                    else
                    {
                        alignment[obj.id].states_set.Remove(Utilities.ObjectState.OFF);
                        alignment[obj.id].states_set.Add(Utilities.ObjectState.ON);
                    }

                    foreach (HandInteraction.ActivationSwitch asw in hi.switches)
                    {
                        if (asw.action == HandInteraction.ActivationAction.SwitchOn)
                        {
                            if (asw.transitionSequences?.Count > 0 && asw.transitionSequences[0].transitions?.Count > 0)
                            {
                                foreach (HandInteraction.TransitionSequence ts in asw.transitionSequences)
                                {
                                    ts.transitions[0].Flip();
                                }
                                break;
                            }
                        }
                    }
                }
            }

            if (obj.states_set.Contains(ObjectState.SITTING) && !sceneStates.Contains(ObjectState.SITTING)) {
                ExecuteInstantSit(obj);
            } else if (!obj.states_set.Contains(ObjectState.SITTING) && sceneStates.Contains(ObjectState.SITTING)) {
                ExecuteInstantStandup(obj);
            }
        }

        private void ExecuteInstantStandup(EnvironmentObject obj)
        {
            expanderResult.enumerators.Add(StandupEnumerator(obj.transform));
        }

        private void ExecuteInstantSit(EnvironmentObject obj)
        {
            List<EnvironmentObject> objsInRelation = GetObjectsInRelation(edgeMap, obj, ObjectRelation.ON);
            EnvironmentObject sittableObj = objsInRelation.Find(o => o.properties.Contains("SITTABLE"));

            if (sittableObj == null) {
                throw new ExpanderException(true, "Sitting destination not specified (ON relaion is missing)");
            }

            Bounds bounds = GameObjectUtils.GetBounds(sittableObj.transform.gameObject);

            if (sittableObj == null || bounds.size == Vector3.zero)
                return;

            Vector3 closest = bounds.ClosestPoint(obj.transform.position);
            Vector3 sitDir = sittableObj.transform.TransformDirection(Vector3.right);
            Vector3 centerSitPos = bounds.ClosestPoint(bounds.center + 4 * sitDir);

            Vector3 newPosition = obj.transform.position;

            if (Vector3.Angle(sitDir, closest - centerSitPos) < 90) {
                if (Vector3.Distance(closest, obj.transform.position) > 0.5f) {
                    newPosition = closest + 0.4f * sitDir;
                }
            } else {
                newPosition = centerSitPos + 0.4f * sitDir;
            }

            newPosition.y = 0;

            Bounds charSitBounds = new Bounds(newPosition, new Vector3(0.3f, 0.3f, 0.3f));
            List<Vector3> sitPositions = GameObjectUtils.CalculatePutPositions(newPosition, charSitBounds, newPosition, sittableObj.transform.gameObject, 
                false, IgnoreObstacles);

            if (sitPositions.Count > 0) {
                newPosition = sitPositions[0] + new Vector3(0, -0.60f, 0);
                if (newPosition.y < 0)
                    newPosition.y = 0;
            }

            //NavMeshAgent nma = obj.transform.GetComponent<NavMeshAgent>();

            obj.transform.rotation = Quaternion.LookRotation(sitDir);
            //nma.Warp(newPosition + 0.3f * sitDir);

            expanderResult.enumerators.Add(SitEnumerator(obj.transform, newPosition));
        }

        IEnumerator SitEnumerator(Transform charTransform, Vector3 newPosition)
        {
            NavMeshAgent nma = charTransform.GetComponent<NavMeshAgent>();
            Animator animator = charTransform.GetComponent<Animator>();

            //foreach (GameObject go in ScriptUtils.FindAllObjects(charTransform)) {
            //    Collider co = go.GetComponent<Collider>();

            //    if (co != null) {
            //        co.enabled = false;
            //    }
            //}

            nma.enabled = false;
            animator.speed = 150.0f;
            animator.SetBool("Sit", true);
            float sitWeight;
            do {
                sitWeight = animator.GetFloat("SitWeight");
                yield return null;
            } while (sitWeight < 0.99f);
            animator.speed = 0.0f;

            charTransform.position = newPosition;

        }

        IEnumerator AnimatedGotoEnumerator(CharacterControl cc, IEnumerable<Vector3> positions)
        {
            return cc.walkOrRunTo(true, positions, Enumerable.Empty<Vector3>());
        }

        IEnumerator StandupEnumerator(Transform charTransform)
        {
            NavMeshAgent nma = charTransform.GetComponent<NavMeshAgent>();
            Animator animator = charTransform.GetComponent<Animator>();

            //foreach (GameObject go in ScriptUtils.FindAllObjects(charTransform)) {
            //    Collider co = go.GetComponent<Collider>();

            //    if (co != null) {
            //        co.enabled = true;
            //    }
            //}

            animator.speed = 150.0f;
            animator.SetBool("Sit", false);

            float sitWeight;

            do {
                sitWeight = animator.GetFloat("SitWeight");
                yield return null;
            } while (sitWeight > 0.01f);
            animator.speed = 0.0f;
            nma.enabled = true;
        }

        private bool TryPlaceObject(EnvironmentObject src, EnvironmentObject dest, bool inside)
        {
            if (src.class_name != "computer") {
                return TryPlaceSingleObject(src, dest, Vector3.zero, inside);   
            } else {
                EnvironmentObject cpuScreen = src.Copy();
                cpuScreen.class_name = "cpuscreen";
                cpuScreen.id++;
                return TryPlaceSingleObject(cpuScreen, dest, new Vector3(0, 0, 0.3f), false) &&
                    TryPlaceSingleObject(src, dest, new Vector3(0, 0, -0.3f), false);
            }
        }

        private bool TryPlaceSingleObject(EnvironmentObject src, EnvironmentObject dest, Vector3 centerDelta, bool inside)
        {
            GameObject newGo = TryPlaceObject(src.class_name, dest, centerDelta, inside);

            if (newGo == null) {
                return false;
            } else {
                src.transform = newGo.transform;
                src.bounding_box = ObjectBounds.FromGameObject(newGo);

                EnvironmentObject srcCopy = src.Copy();
                srcCopy.states_set = EnvironmentGraphCreator.GetDefaultObjectStates(newGo, srcCopy.class_name, srcCopy.properties);

                alignment[src.id] = srcCopy;
                
                return true;
            }
        }

        private GameObject TryPlaceObject(string srcClassName, EnvironmentObject dest, Vector3 centerDelta, bool inside)
        {
            List<string> names = dataProviders.NameEquivalenceProvider.GetEquivalentNames(srcClassName);
            int numPrefabsChecked = 0;
            
            if (Randomize) {
                RandomUtils.Permute(names);
            }
            foreach (string name in names) {
                List<string> fileNames;

                if (TryGetAssets(name, out fileNames)) {
                    GameObject newGo;

                    if (Randomize) {
                        fileNames = RandomUtils.RanomPermutation(fileNames);
                    }

                    foreach (string fileName in fileNames) {

                        numPrefabsChecked++;
                        if (PlaceObject(inside, srcClassName, fileName, dest, centerDelta, out newGo)) {
                            // Debug.Log($"Put {srcClassName} to {dest.class_name}, inside: {inside}");
                            return newGo;
                        }
                    }
                }
            }
            if (numPrefabsChecked == 0) {
                // Debug.Log($"Cannot find asset for {srcClassName}");
                expanderResult.AddItem(MISSING_PREF, srcClassName);
            }
            return null;
        }

        private bool TryGetAssets(string name, out List<string> fileNames)
        {
            if (AssetsMap == null) {
                return dataProviders.AssetsProvider.TryGetAssets(name, out fileNames);
            } else {
                return AssetsMap.TryGetValue(name.ToLower(), out fileNames);
            }
        }

        private bool PlaceObject(bool inside, string className, string prefabFile, EnvironmentObject destObj, Vector3 centerDelta, out GameObject newGo)
        {
            GameObject loadedObj = Resources.Load(ScriptUtils.TransformResourceFileName(prefabFile)) as GameObject;
            if (loadedObj == null)
            {
                newGo = null;
                return false;
            }
            if (GameObjectUtils.GetCollider(loadedObj) == null) {
                // If there are no colliders on destination object
                newGo = null;
                return false;
            }
            List<Vector3> positions = new List<Vector3>();

            try
            {
                positions = GameObjectUtils.CalculatePutPositions(destObj.bounding_box.bounds.center + centerDelta, loadedObj,
                    destObj.transform.gameObject, inside, IgnoreObstacles);
            }
            catch (Exception e)
            {
                throw new ExpanderException(true, "Bounds of object " + destObj.class_name + " are not defined when placing " + className);
            }
            if (positions.Count == 0) {
                newGo = null;
                return false;
            } else {
                Transform destObjRoom = GameObjectUtils.GetRoomTransform(destObj.transform);
                newGo = UnityEngine.Object.Instantiate(loadedObj, destObjRoom) as GameObject;
                newGo.transform.position = Randomize ? RandomUtils.Choose(positions) : positions[0];
                newGo.name = loadedObj.name;
                ObjectAnnotator.AnnotateObj(newGo.transform);
                ColorEncoding.EncodeGameObject(newGo);
                return true;
            }
        }

        private bool MoveObject(bool inside, EnvironmentObject srcObject, EnvironmentObject destObj)
        {
            if (srcObject.transform == null) {
                expanderResult.AddItem(MISSING_INT, srcObject.class_name + "." + srcObject.id.ToString());
                return false;
            }
            if (destObj.transform == null) {
                expanderResult.AddItem(MISSING_INT, destObj.class_name + "." + srcObject.id.ToString());
                return false;
            }

            List<Vector3> positions = GameObjectUtils.CalculatePutPositions(destObj.bounding_box == null ? destObj.transform.position :
                    destObj.bounding_box.bounds.center,
                srcObject.transform.gameObject,
                destObj.transform.gameObject, inside, IgnoreObstacles);
            if (positions.Count == 0) {
                return false;
            } else {
                srcObject.transform.position = positions[0];
                return true;
            }
        }

        static List<EnvironmentObject> GetObjectsInRelation(Dictionary<Tuple<int, ObjectRelation>, List<EnvironmentObject>> edgeMap,
            EnvironmentObject from, ObjectRelation relation)
        {
            if (from == null)
                return new List<EnvironmentObject>();

            List<EnvironmentObject> result;

            if (edgeMap.TryGetValue(Tuple.Create(from.id, relation), out result)) return result;
            else return new List<EnvironmentObject>();
        }
    }

    class CameraExpander
    {
        public const string FRONT_CHARACTER_CAMERA_NAME = "Character_Camera_Front";
        public const string TOP_CHARACTER_CAMERA_NAME = "Character_Camera_Top";
        public const string FORWARD_VIEW_CAMERA_NAME = "Character_Camera_Fwd";
        public const string FROM_BACK_CAMERA_NAME = "Character_Camera_FBack";
        public const string FROM_LEFT_CAMERA_NAME = "Character_Camera_FLeft";

        public static List<Camera> AddRoomCameras(Transform transform)
        {
            List<GameObject> rooms = ScriptUtils.FindAllRooms(transform);
            List<Camera> newCameras = new List<Camera>();
            Bounds sceneBounds = new Bounds();

            foreach (GameObject room in rooms) {
                Bounds bounds = GameObjectUtils.GetRoomBounds(room);
                newCameras.Add(CreateCamera(bounds, room.transform));

                if (sceneBounds.extents == Vector3.zero) sceneBounds = bounds;
                else sceneBounds.Encapsulate(bounds);
            }
            newCameras.Add(CreateCamera(sceneBounds, transform));
            return newCameras;
        }

        public static List<Camera> AddCharacterCameras(GameObject character, Transform transform, string cameraName)
        {
            List<Camera> newCameras = new List<Camera>();

            switch (cameraName)
            {
                case FORWARD_VIEW_CAMERA_NAME:
                    {
                        // Forward-view camera
                        GameObject go = new GameObject(FORWARD_VIEW_CAMERA_NAME, typeof(Camera));
                        Camera camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 1.8f, 0.15f);
                        go.transform.localRotation = Quaternion.Euler(30, 0, 0);

                        newCameras.Add(camera);
                    }
                    break;
                case TOP_CHARACTER_CAMERA_NAME:
                    {
                        // Top-view camera
                        GameObject go = new GameObject(TOP_CHARACTER_CAMERA_NAME, typeof(Camera));
                        Camera camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 4.5f, 0);
                        go.transform.localRotation = Quaternion.LookRotation(Vector3.down);

                        newCameras.Add(camera);
                    }
                    break;
                case FROM_BACK_CAMERA_NAME:
                    {
                        // Top-view camera
                        GameObject go = new GameObject(FROM_BACK_CAMERA_NAME, typeof(Camera));
                        Camera camera = go.GetComponent<Camera>();

                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        //// go.hideFlags = HideFlags.HideAndDontSave;
                        //go.AddComponent(typeof(CameraFollow));
                        //go.GetComponent<CameraFollow>().target = character.transform;
                        //go.GetComponent<CameraFollow>().offset_pos = new Vector3(0, 2.0f, -1.2f);
                        //go.GetComponent<CameraFollow>().offset_rot = Quaternion.Euler(20, 0, 0);

                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 2.0f, -1.2f);
                        go.transform.localRotation = Quaternion.Euler(20, 0, 0);

                        //go.transform.localPosition = new Vector3(-1.4f, 1.7f, 1.0f);
                        //go.transform.localRotation = Quaternion.Euler(20, 120, 0);


                        newCameras.Add(camera);
                    }
                    break;
                case FROM_LEFT_CAMERA_NAME:
                    {
                        // Top-view camera
                        GameObject go = new GameObject(FROM_LEFT_CAMERA_NAME, typeof(Camera));
                        Camera camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;


                        go.transform.localPosition = new Vector3(-0.7f, 1.9f, 0.6f);
                        go.transform.localRotation = Quaternion.Euler(32, 120, 0);


                        newCameras.Add(camera);
                    }
                    break;

                default:
                    {
                        // Top-view camera
                        GameObject go = new GameObject(TOP_CHARACTER_CAMERA_NAME, typeof(Camera));
                        Camera camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 4.5f, 0);
                        go.transform.localRotation = Quaternion.LookRotation(Vector3.down);

                        newCameras.Add(camera);

                        // Forward-view camera
                        go = new GameObject(FORWARD_VIEW_CAMERA_NAME, typeof(Camera));
                        camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;
                        camera.nearClipPlane = 0.1f;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 1.8f, 0.15f);
                        go.transform.localRotation = Quaternion.Euler(20, 0, 0);

                        newCameras.Add(camera);

                        // Right-view camera
                        go = new GameObject("Character_Camera_right", typeof(Camera));
                        camera = go.GetComponent<Camera>();

                        go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 1.8f, 0.3f);
                        go.transform.localRotation = Quaternion.Euler(20, 90, 0);

                        newCameras.Add(camera);

                        // Left-view camera
                        go = new GameObject("Character_Camera_left", typeof(Camera));
                        camera = go.GetComponent<Camera>();

                        go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 1.8f, 0.3f);
                        go.transform.localRotation = Quaternion.Euler(20, -90, 0);

                        newCameras.Add(camera);

                        // Back-view camera
                        go = new GameObject("Character_Camera_back", typeof(Camera));
                        camera = go.GetComponent<Camera>();

                        go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;
                        go.transform.localPosition = new Vector3(0, 1.8f, -0.3f);
                        go.transform.localRotation = Quaternion.Euler(20, 180, 0);
                        newCameras.Add(camera);

                        // Front-view camera
                        go = new GameObject(FRONT_CHARACTER_CAMERA_NAME, typeof(Camera));
                        camera = go.GetComponent<Camera>();
                        camera.renderingPath = RenderingPath.UsePlayerSettings;

                        // go.hideFlags = HideFlags.HideAndDontSave;
                        go.transform.parent = character.transform;

                        // Temporary, will be changed when requested
                        go.transform.localPosition = 1.5f * Vector3.forward;
                        go.transform.localRotation = Quaternion.LookRotation(Vector3.back);

                        newCameras.Add(camera);
                    }
                    break;
            }
            //for (int i = 0; i < newCameras.Count; i++)
            //{
            //    //CameraUtils.InitCamera(newCameras[i]);
            //    newCameras[i].enabled = false;
            //}
            return newCameras;
        }

        private static Camera CreateCamera(Bounds bounds, Transform room)
        {
            GameObject go = new GameObject("Room_Camera_" + room.name, typeof(Camera));
            Camera camera = go.GetComponent<Camera>();
            float fow = camera.fieldOfView;
            float maxExt = Mathf.Max(bounds.extents.x, bounds.extents.z);
            float dist = maxExt / (Mathf.Tan(Mathf.Deg2Rad * fow / 2));

            camera.nearClipPlane = dist + 0.1f;
            // go.hideFlags = HideFlags.HideAndDontSave;
            go.transform.parent = room;
            go.transform.position = new Vector3(bounds.center.x, bounds.max.y + dist, bounds.center.z);
            go.transform.LookAt(bounds.center);
            return camera;
        }


        public static void AdjustCamera(Camera camera)
        {
            // We only adjust front camera
            if (camera.name == FRONT_CHARACTER_CAMERA_NAME) {
                CameraUtils.AdjustFrontCharacterCamera(camera);
            }
        }

    }

    public class PointCloudExporter
    {
        static readonly string[] ExcludedCategories = { "Rooms", "Floor", "Walls", "Ceiling" };

        private float minDistance;


        public PointCloudExporter(float minDistance)
        {
            this.minDistance = minDistance;
        }

        public List<ObjectPointCloud> ExportObjects(ICollection<EnvironmentObject> objects)
        {
            List<ObjectPointCloud> result = new List<ObjectPointCloud>();

            foreach (EnvironmentObject eo in objects) {
                if (Array.IndexOf(ExcludedCategories, eo.category) < 0) {
                    ObjectPointCloud opc = ExportObject(eo);

                    if (opc != null) {
                        result.Add(opc);
                    }
                }
            }
            return result;
        }

        public ObjectPointCloud ExportObject(EnvironmentObject obj)
        {
            if (obj == null || obj.transform == null)
                return null;

            Mesh mesh = FindMesh(obj.transform);
            Comparer<Vector3> xComparer = Comparer<Vector3>.Create((v1, v2) => v1.x.CompareTo(v2.x));

            if (mesh == null)
                return null;

            List<Vector3> xPoints = new List<Vector3>();
            ObjectPointCloud opc = new ObjectPointCloud() { id = obj.id, points = new List<float[]>() };

            foreach (Vector3 mv in mesh.vertices) {
                Vector3 p = obj.transform.TransformPoint(mv);
                int index = xPoints.BinarySearch(p, xComparer);
                bool skip = false;

                if (index < 0)
                    index = ~index;

                for (int i = index; i < xPoints.Count && !skip; i++) {
                    Vector3 v = xPoints[i];
                    if (Vector3.Distance(v, p) < minDistance)
                        skip = true;
                    if (Mathf.Abs(v.x - p.x) > minDistance)
                        break;
                }
                for (int i = index - 1; i >= 0 && !skip; i--) {
                    Vector3 v = xPoints[i];
                    if (Vector3.Distance(v, p) < minDistance)
                        skip = true;
                    if (Mathf.Abs(v.x - p.x) > minDistance)
                        break;
                }
                if (!skip) {
                    xPoints.Insert(index, p);
                    opc.points.Add(new float[] { p.x, p.y, p.z });
                }
            }
            return opc;
        }

        private Mesh FindMesh(Transform t)
        {
            foreach (GameObject go in ScriptUtils.FindAllObjects(t)) {
                MeshFilter mf = go.GetComponent<MeshFilter>();

                if (mf != null) {
                    return mf.mesh;
                }
            }
            return null;
        }
    }

    // Json object
    public class ObjectPointCloud
    {
        public int id;
        public IList<float[]> points;
    }



}