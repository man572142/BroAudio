using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

public class FieldUsageFinder : EditorWindow
{
    public static void ShowWindow()
    {
        GetWindow<FieldUsageFinder>("Field Usage Finder");
    }
    
    [Flags]
    private enum AssemblyFilter
    {
        None = 0,
        System = 1 << 0,
        Unity = 1 << 1,
        JetBrains = 1 << 2,
        Mono = 1 << 3,
    }

    private enum ColumnType
    {
        Source,
        GameObjectPath,
        ComponentType,
        FieldName,
        Value,
        FullAssetPath,
    }

    private class UsageResult
    {
        public string AssetPath;
        public string AssetFileName;
        public string GameObjectPath;
        public string ComponentType;
        public string FieldName;
        public string Value;
        public UnityEngine.Object Asset;
        public GameObject GameObject;
    }
    
    private class TypeDropdown : AdvancedDropdown
    {
        private readonly Dictionary<int, Type> _typeMap = new Dictionary<int, Type>();
        private readonly FieldUsageFinder _window;

        public TypeDropdown(AdvancedDropdownState state, FieldUsageFinder window) : base(state)
        {
            _window = window;
            minimumSize = new Vector2(300, 400);
        }

        protected override AdvancedDropdownItem BuildRoot()
        {
            var root = new AdvancedDropdownItem("Types");
            _typeMap.Clear();

            var allTypes = new List<Type>();
#if UNITY_6000_4_OR_NEWER
            foreach (var assembly in UnityEngine.Assemblies.CurrentAssemblies.GetLoadedAssemblies())
#else
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
#endif
            {
                if (_window.ShouldExcludeAssembly(assembly))
                    continue;

                var types = assembly.GetTypes()
                    .Where(t => (t.IsPublic || t.IsNestedPublic) &&
                                !t.IsGenericTypeDefinition && !t.IsAbstract && !t.IsInterface)
                    .OrderBy(t => t.FullName);
                allTypes.AddRange(types);
            }
            
            var typesByAssembly = allTypes.GroupBy(t => t.Assembly.GetName().Name);

            foreach (var assemblyGroup in typesByAssembly.OrderBy(g => g.Key))
            {
                var assemblyItem = new AdvancedDropdownItem($"{assemblyGroup.Key}");
                
                var typesByNamespace = assemblyGroup.GroupBy(t => t.Namespace ?? "<global>");
                foreach (var namespaceGroup in typesByNamespace.OrderBy(g => g.Key))
                {
                    var namespaceItem = new AdvancedDropdownItem($"{namespaceGroup.Key}");

                    foreach (var type in namespaceGroup.OrderBy(t => t.Name))
                    {
                        var typeItem = new AdvancedDropdownItem(type.Name) { id = _typeMap.Count };
                        _typeMap[typeItem.id] = type;
                        namespaceItem.AddChild(typeItem);
                    }

#if UNITY_6000_5_OR_NEWER
                    if (namespaceItem.childList.Count > 0)
#else
                    if (namespaceItem.children.Any())
#endif
                    {
                        assemblyItem.AddChild(namespaceItem);
                    }
                }

#if UNITY_6000_5_OR_NEWER
                if (assemblyItem.childList.Count > 0)
#else
                if (assemblyItem.children.Any())
#endif
                {
                    root.AddChild(assemblyItem);
                }
            }
            return root;
        }

        protected override void ItemSelected(AdvancedDropdownItem item)
        {
            if (_typeMap.TryGetValue(item.id, out Type selectedType))
            {
                _window._selectedType = selectedType;
                _window.Repaint();
            }
        }
    }

    private static readonly Dictionary<AssemblyFilter, string[]> AssemblyPrefixes = new Dictionary<AssemblyFilter, string[]>
    {
        { AssemblyFilter.System, new[] { "System.", "System", "mscorlib", "netstandard" } },
        { AssemblyFilter.Unity, new[] { "UnityEngine", "UnityEditor", "Unity.", "unityplastic", "ExCSS.Unity" } },
        { AssemblyFilter.JetBrains, new[] { "JetBrains." } },
        { AssemblyFilter.Mono, new[] { "Mono." } }
    };
    
    private const string RootPath = "Assets";
    private const int MaxSearchDepth = 10;
    
    private Type _selectedType;
    private TypeDropdown _typeDropdown;
    private AssemblyFilter _excludedAssemblies = AssemblyFilter.System | AssemblyFilter.Unity | AssemblyFilter.Mono;
    private string _targetPath = RootPath;
    private bool _findInPrefab;
    private bool _findInScriptableObject;
    private bool _findInScene;
    private Vector2 _scrollPosition;
    private readonly List<UsageResult> _results = new List<UsageResult>();

    /// <summary>
    /// Sets the target type to search for programmatically.
    /// </summary>
    /// <param name="type">The type to search for in the project assets.</param>
    protected void SetTargetType(Type type)
    {
        if (type == null)
        {
            Debug.LogError("Cannot set null as target type.");
            return;
        }

        _selectedType = type;
        Repaint();
    }

    private MultiColumnHeader _multiColumnHeader;
    private MultiColumnHeaderState _multiColumnHeaderState;
    private int _sortedColumnIndex = -1;
    private bool _sortedAscending = true;

    private bool ShouldExcludeAssembly(Assembly assembly)
    {
        var assemblyName = assembly.GetName().Name;
        
        foreach (var filter in AssemblyPrefixes)
        {
            if (_excludedAssemblies.HasFlag(filter.Key) && 
                filter.Value.Any(prefix => assemblyName.StartsWith(prefix) || 
                                           assemblyName.Equals(prefix.TrimEnd('.'))))
            {
                return true;
            }
        }
        return false;
    }
    
    protected virtual void OnEnable()
    {
        InitializeMultiColumnHeader();
        
        var dropdownState = new AdvancedDropdownState();
        _typeDropdown = new TypeDropdown(dropdownState, this);
    }

    private void InitializeMultiColumnHeader()
    {
        var columns = new MultiColumnHeaderState.Column[]
        {
            CreateColumn("Source", 100),
            CreateColumn("GameObject", 150),
            CreateColumn("Component", 120),
            CreateColumn("Field", 100),
            CreateColumn("Value", 150),
            CreateColumn("Asset Path", 200),
        };

        _multiColumnHeaderState = new MultiColumnHeaderState(columns);
        _multiColumnHeader = new MultiColumnHeader(_multiColumnHeaderState);
        _multiColumnHeader.sortingChanged += OnSortingChanged;
        _multiColumnHeader.ResizeToFit();
    }
    
    private MultiColumnHeaderState.Column CreateColumn(string header, float width)
    {
        return new MultiColumnHeaderState.Column
        {
            headerContent = new GUIContent(header),
            headerTextAlignment = TextAlignment.Left,
            sortedAscending = true,
            sortingArrowAlignment = TextAlignment.Center,
            width = width,
            minWidth = 50,
            autoResize = true,
            allowToggleVisibility = true,
        };
    }

    private void OnSortingChanged(MultiColumnHeader multiColumnHeader)
    {
        _sortedColumnIndex = multiColumnHeader.sortedColumnIndex;
        _sortedAscending = multiColumnHeader.GetColumn(_sortedColumnIndex).sortedAscending;
        SortResults();
        Repaint();
    }

    private void SortResults()
    {
        if (_sortedColumnIndex < 0 || _results.Count == 0)
            return;

        var columnType = (ColumnType)_sortedColumnIndex;
        switch (columnType)
        {
            case ColumnType.Source:
                _results.Sort((a, b) => Compare(a.AssetFileName, b.AssetFileName));
                break;
            case ColumnType.GameObjectPath:
                _results.Sort((a, b) => Compare(a.GameObjectPath, b.GameObjectPath));
                break;
            case ColumnType.ComponentType:
                _results.Sort((a, b) => Compare(a.ComponentType, b.ComponentType));
                break;
            case ColumnType.FieldName:
                _results.Sort((a, b) => Compare(a.FieldName, b.FieldName));
                break;
            case ColumnType.Value:
                _results.Sort((a, b) => Compare(a.Value, b.Value));
                break;
        }
        
        int Compare(string a, string b) => _sortedAscending ? 
            string.Compare(a, b, StringComparison.Ordinal) : string.Compare(b, a, StringComparison.Ordinal);
    }

    private void OnGUI()
    {
        GUILayout.Label("Asset Type Usage Finder", EditorStyles.boldLabel);
        EditorGUILayout.Space();
        
        _excludedAssemblies = (AssemblyFilter)EditorGUILayout.EnumFlagsField("Exclude Assemblies", _excludedAssemblies);
        DrawTypeSelectionDropdown();
        
        bool isValidPath = _targetPath.StartsWith(RootPath);
        if (!isValidPath)
        {
            EditorGUILayout.HelpBox("The path must be under the Assets folder or its subfolders", MessageType.Error);
        }
        DrawPathTextField();

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Find In:");
        _findInPrefab = EditorGUILayout.ToggleLeft("Prefabs", _findInPrefab);
        _findInScriptableObject = EditorGUILayout.ToggleLeft("Scriptable Objects", _findInScriptableObject);
        _findInScene = EditorGUILayout.ToggleLeft("Scenes", _findInScene);
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space();

        bool hasAnyTarget = _findInPrefab || _findInScriptableObject || _findInScene;
        using (new EditorGUI.DisabledScope(!isValidPath || _selectedType == null || !hasAnyTarget))
        {
            if (GUILayout.Button("Find Usage"))
            {
                FindTypeUsage();
            }
        }

        if (_selectedType == null)
        {
            EditorGUILayout.HelpBox("Please select a type from the dropdown.", MessageType.Warning);
        }
        else if (!hasAnyTarget)
        {
            EditorGUILayout.HelpBox("Please select at least one target asset type to search.", MessageType.Warning);
        }
        EditorGUILayout.Space();
        
        if (_results.Count > 0)
        {
            GUILayout.Label($"Found {_results.Count} usage(s):", EditorStyles.boldLabel);
            
            var headerRect = GUILayoutUtility.GetRect(0, _multiColumnHeader.height, GUILayout.ExpandWidth(true));
            _multiColumnHeader.OnGUI(headerRect, 0.0f);
            
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            for (int i = 0; i < _results.Count; i++)
            {
                var result = _results[i];
                var rowRect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight, GUILayout.ExpandWidth(true));
                
                if (i % 2 == 0)
                {
                    EditorGUI.DrawRect(rowRect, new Color(0f, 0f, 0f, 0.1f));
                }
                DrawResultRow(rowRect, result);
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawTypeSelectionDropdown()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel("Target Type");
            
            var buttonContent = _selectedType != null 
                ? new GUIContent(_selectedType.Name, _selectedType.FullName)
                : new GUIContent("Select Type...");
            
            var buttonRect = GUILayoutUtility.GetRect(buttonContent, EditorStyles.popup);
            
            if (GUI.Button(buttonRect, buttonContent, EditorStyles.popup))
            {
                _typeDropdown.Show(buttonRect);
            }
        }
        
        if (_selectedType != null)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(EditorGUIUtility.labelWidth);
                EditorGUILayout.LabelField($"{_selectedType.FullName} (Assembly: {_selectedType.Assembly.GetName().Name})", EditorStyles.miniLabel);
            }
        }
    }

    private void DrawResultRow(Rect rowRect, UsageResult result)
    {
        float[] columnWidths = new float[_multiColumnHeaderState.columns.Length];
        for (int i = 0; i < _multiColumnHeaderState.columns.Length; i++)
        {
            columnWidths[i] = _multiColumnHeaderState.columns[i].width;
        }

        Rect[] columnRects = new Rect[columnWidths.Length];
        float x = rowRect.x;
        for (int i = 0; i < columnWidths.Length; i++)
        {
            columnRects[i] = new Rect(x, rowRect.y, columnWidths[i], rowRect.height);
            x += columnWidths[i];
        }
        
        if (GUI.Button(columnRects[(int)ColumnType.Source], result.AssetFileName, EditorStyles.linkLabel))
        {
            if (result.GameObject != null)
            {
                SelectObject(result.GameObject);
            }
            else if (result.Asset != null)
            {
                SelectObject(result.Asset);
            }
            else if (result.AssetFileName.EndsWith(".unity"))
            {
                LoadSceneAndSelectObject(result.AssetPath, result.GameObjectPath);
            }
        }
        
        GUI.Label(columnRects[(int)ColumnType.GameObjectPath], result.GameObjectPath);
        GUI.Label(columnRects[(int)ColumnType.ComponentType], result.ComponentType);
        GUI.Label(columnRects[(int)ColumnType.FieldName], result.FieldName);
        GUI.Label(columnRects[(int)ColumnType.Value], result.Value);
        GUI.Label(columnRects[(int)ColumnType.FullAssetPath], result.AssetPath);
    }

    private static void SelectObject<T>(T obj) where T : UnityEngine.Object
    {
        if (obj is GameObject go)
        {
            Selection.activeGameObject = go;
        }
        else
        {
            Selection.activeObject = obj;
        }
        EditorGUIUtility.PingObject(obj);
    }

    private void DrawPathTextField()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            _targetPath = EditorGUILayout.TextField("Target Path", _targetPath);
            if (GUILayout.Button("...", GUILayout.Width(30f)))
            {
                string path = EditorUtility.OpenFolderPanel("Select Target Path", _targetPath, "");
                
                if (path.Contains(Application.dataPath))
                {
                    _targetPath = path.Remove(0, Application.dataPath.Length - RootPath.Length);;
                }
                else
                {
                    Debug.LogError("The path must be under the Assets folder or its subfolders");
                }
            }
        }
    }

    private void FindTypeUsage()
    {
        _results.Clear();
        
        if (_selectedType == null)
        {
            Debug.LogError("No type selected. Please select a type from the dropdown.");
            return;
        }

        var allAssetPaths = AssetDatabase.GetAllAssetPaths();
        int currentAsset = 0;
        try
        {
            foreach (var path in allAssetPaths)
            {
                currentAsset++;
                if (EditorUtility.DisplayCancelableProgressBar(
                        "Searching for Type Usage", 
                        $"Checking: {path}", currentAsset / (float)allAssetPaths.Length))
                {
                    break;
                }

                if (!path.StartsWith(_targetPath))
                {
                    continue;
                }

                if (_findInPrefab && path.EndsWith(".prefab"))
                {
                    CheckPrefab(path, _selectedType);
                } 
                else if (_findInScene && path.EndsWith(".unity"))
                {
                    CheckScene(path, _selectedType);
                }
                else if (_findInScriptableObject && path.EndsWith(".asset"))
                {
                    CheckScriptableObject(path, _selectedType);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        
        if (_sortedColumnIndex >= 0)
        {
            SortResults();
        }

        Debug.Log($"Search completed. Found {_results.Count} usage(s) of {_selectedType.Name}");
        Repaint();
    }

    private void CheckPrefab(string assetPath, Type targetType)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        if (prefab != null)
        {
            CheckGameObjectHierarchy(prefab, assetPath, targetType);
        }
    }

    private void CheckScene(string assetPath, Type targetType)
    {
        var currentScene = EditorSceneManager.GetActiveScene();
        bool wasCurrentSceneDirty = currentScene.isDirty;

        // Load the scene additively to avoid disrupting the current scene
        Scene scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
            
        if (scene.IsValid())
        {
            GameObject[] rootObjects = scene.GetRootGameObjects();
            foreach (GameObject rootObject in rootObjects)
            {
                CheckGameObjectHierarchy(rootObject, assetPath, targetType);
            }
                
            EditorSceneManager.CloseScene(scene, true);
        }

        // Restore scene dirty state
        if (!wasCurrentSceneDirty && currentScene.isDirty)
        {
            EditorSceneManager.MarkSceneDirty(currentScene);
        }
    }

    private void CheckScriptableObject(string assetPath, Type targetType)
    {
        ScriptableObject scriptableObject = AssetDatabase.LoadAssetAtPath<ScriptableObject>(assetPath);
        if (scriptableObject != null)
        {
            int depth = 0;
            CheckObjectFields(scriptableObject, assetPath, "", scriptableObject.GetType().Name, targetType, ref depth);
        }
    }

    private void CheckGameObjectHierarchy(GameObject gameObject, string assetPath, Type targetType)
    {
        CheckGameObject(gameObject, assetPath, targetType);
        
        for (int i = 0; i < gameObject.transform.childCount; i++)
        {
            CheckGameObjectHierarchy(gameObject.transform.GetChild(i).gameObject, assetPath, targetType);
        }
    }

    private void CheckGameObject(GameObject gameObject, string assetPath, Type targetType)
    {
        Component[] components = gameObject.GetComponents<Component>();
        
        foreach (Component component in components)
        {
            if (component == null) continue;
            int depth = 0;
            CheckObjectFields(component, assetPath, GetGameObjectPath(gameObject), component.GetType().Name, targetType, ref depth);
        }
    }

    private void CheckObjectFields(object obj, string assetPath, string gameObjectPath, string componentType, Type targetType, ref int depth)
    {
        if (obj == null || depth > MaxSearchDepth) return;

        Type objectType = obj.GetType();
        FieldInfo[] fields = objectType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        depth++;

        foreach (FieldInfo field in fields)
        {
            if(field.IsPrivate && field.GetCustomAttribute<SerializeField>() == null) continue;

            if (DoesFieldUseType(field, targetType, assetPath, gameObjectPath, componentType, obj, ref depth))
            {
                _results.Add(new UsageResult
                {
                    AssetPath = assetPath,
                    AssetFileName = assetPath.Substring(assetPath.LastIndexOf('/') + 1),
                    GameObjectPath = gameObjectPath,
                    ComponentType = componentType,
                    FieldName = field.Name,
                    Value = GetValueString(field.GetValue(obj)),
                    Asset = obj as UnityEngine.Object,
                    GameObject = obj is Component comp ? comp.gameObject : null
                });
            }
        }
    }
    
    private bool DoesFieldUseType(FieldInfo field, Type targetType, string assetPath, string gameObjectPath,
        string componentType, object obj, ref int depth)
    {
        if (obj == null)
        {
            return false;
        }
        
        Type fieldType = field.FieldType;
        // Direct type match
        if (fieldType == targetType)
            return true;

        // Array type
        if (fieldType.IsArray && fieldType.GetElementType() == targetType)
            return true;
        
        // List type
        if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
        {
            Type[] genericArgs = fieldType.GetGenericArguments();
            foreach (Type genericArg in genericArgs)
            {
                if (genericArg == targetType)
                    return true;
            }
        }
        
        // Serializable type
        if (!fieldType.IsPrimitive && fieldType.GetCustomAttribute<SerializableAttribute>() != null)
        {
            var value = field.GetValue(obj);
            CheckObjectFields(value, assetPath, gameObjectPath, componentType, targetType, ref depth);
        }
        return false;
    }
    
    protected virtual string GetValueString(object value)
    {
        if (value == null)
        {
            return "null";
        }
        return value.ToString();
    }

    private string GetGameObjectPath(GameObject gameObject)
    {
        string path = gameObject.name;
        Transform current = gameObject.transform.parent;
        
        while (current != null)
        {
            path = current.name + "/" + path;
            current = current.parent;
        }
        
        return path;
    }

    private void LoadSceneAndSelectObject(string scenePath, string gameObjectPath)
    {
        Scene currentScene = EditorSceneManager.GetActiveScene();
        if (currentScene.isDirty)
        {
            int option = EditorUtility.DisplayDialogComplex(
                "Unsaved Changes",
                $"The current scene '{currentScene.name}' has unsaved changes. Do you want to save before loading the new scene?",
                "Save",      // option 0
                "Don't Save", // option 1
                "Cancel"     // option 2
            );

            switch (option)
            {
                case 0: // Save
                    EditorSceneManager.SaveScene(currentScene);
                    break;
                case 1: // Don't Save
                    // Continue without saving
                    break;
                case 2: // Cancel
                    return; // Don't load the new scene
            }
        }
        
        EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
        
        if (!string.IsNullOrEmpty(gameObjectPath))
        {
            EditorApplication.delayCall += () => {
                GameObject targetObject = FindGameObjectByPath(gameObjectPath);
                if (targetObject != null)
                {
                    SelectObject(targetObject);
                }
                else
                {
                    Debug.LogWarning($"Could not find GameObject at path '{gameObjectPath}' in scene '{scenePath}'.");
                }
            };
        }
    }

    private GameObject FindGameObjectByPath(string path)
    {
        int firstSlash = path.IndexOf('/');
        var subPath = firstSlash < 0 ? string.Empty : path.Substring(firstSlash + 1);
        var rootName = firstSlash < 0 ? path : path.Substring(0, firstSlash);
        
        GameObject root = EditorSceneManager.GetActiveScene()
            .GetRootGameObjects()
            .FirstOrDefault(go => go.name == rootName);
        
        if (string.IsNullOrEmpty(subPath)) return root;
        
        return root?.transform.Find(subPath)?.gameObject;
    }
}