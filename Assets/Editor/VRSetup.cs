using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Teleportation;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

/// <summary>
/// Rebuilds the project's XR configuration and the sample VR scene from real Unity APIs.
/// Run from Tools > BREAKPOINT > Configure VR Project, or in batch mode:
///   Unity.exe -batchmode -quit -projectPath . -executeMethod VRSetup.RunBatch -logFile Logs\vrsetup.log
/// </summary>
public static class VRSetup
{
    const string XRSettingsAssetPath = "Assets/XR/XRGeneralSettingsPerBuildTarget.asset";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";
    const string RigPrefabPath = "Assets/Samples/XR Interaction Toolkit/3.0.1/Starter Assets/Prefabs/XR Origin (XR Rig).prefab";
    const string MaterialFolder = "Assets/Materials";
    const string OpenXRLoaderType = "UnityEngine.XR.OpenXR.OpenXRLoader";

    static readonly BuildTargetGroup[] XRBuildTargets =
    {
        BuildTargetGroup.Standalone,
        BuildTargetGroup.Android,
    };

    [MenuItem("Tools/BREAKPOINT/Configure VR Project")]
    public static void Run()
    {
        ConfigurePlayerSettings();
        ConfigureXrManagement();
        ConfigureOpenXr();
        BuildScene();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("BREAKPOINT VR setup completed.");
    }

    public static void RunBatch()
    {
        try
        {
            Run();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    [MenuItem("Tools/BREAKPOINT/Build Android APK")]
    public static void BuildAndroid()
    {
        ConfigurePlayerSettings();
        ConfigureXrManagement();
        ConfigureOpenXr();

        Directory.CreateDirectory("Builds");

        var options = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = "Builds/BREAKPOINT.apk",
            target = BuildTarget.Android,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        if (report.summary.result != BuildResult.Succeeded)
            throw new BuildFailedException(
                $"Android build failed: {report.summary.result} ({report.summary.totalErrors} errors).");

        Debug.Log(
            $"Android APK built: {report.summary.outputPath} " +
            $"({report.summary.totalSize / (1024f * 1024f):F1} MB, {report.summary.totalTime}).");
    }

    public static void BuildAndroidBatch()
    {
        try
        {
            BuildAndroid();
            EditorApplication.Exit(0);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
            EditorApplication.Exit(1);
        }
    }

    static void ConfigurePlayerSettings()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear;

        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(
            BuildTarget.Android,
            new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);

        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.androidIsGame = true;
        PlayerSettings.Android.androidTVCompatibility = false;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.LandscapeLeft;

        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenePath, true),
        };

        Debug.Log("Configured Linear color space, Vulkan/GLES3, IL2CPP, ARM64, and Android API 29+.");
    }

    static void ConfigureXrManagement()
    {
        var settings = AssetDatabase.LoadAssetAtPath<XRGeneralSettingsPerBuildTarget>(XRSettingsAssetPath);
        if (settings == null)
        {
            EnsureFolder("Assets/XR");
            settings = ScriptableObject.CreateInstance<XRGeneralSettingsPerBuildTarget>();
            settings.name = "XRGeneralSettingsPerBuildTarget";
            AssetDatabase.CreateAsset(settings, XRSettingsAssetPath);
        }

        EditorBuildSettings.AddConfigObject(XRGeneralSettings.k_SettingsKey, settings, true);

        foreach (var group in XRBuildTargets)
        {
            if (!settings.HasSettingsForBuildTarget(group))
                settings.CreateDefaultSettingsForBuildTarget(group);

            if (!settings.HasManagerSettingsForBuildTarget(group))
                settings.CreateDefaultManagerSettingsForBuildTarget(group);

            var generalSettings = settings.SettingsForBuildTarget(group);
            generalSettings.InitManagerOnStart = true;

            var manager = generalSettings.Manager;
            if (manager == null)
            {
                settings.CreateDefaultManagerSettingsForBuildTarget(group);
                manager = settings.ManagerSettingsForBuildTarget(group);
            }

            if (manager == null)
                throw new InvalidOperationException($"Could not create XR Manager Settings for {group}.");

            if (!XRPackageMetadataStore.IsLoaderAssigned(OpenXRLoaderType, group))
            {
                if (!XRPackageMetadataStore.AssignLoader(manager, OpenXRLoaderType, group))
                    throw new InvalidOperationException($"Could not assign the OpenXR loader for {group}.");
            }

            EditorUtility.SetDirty(generalSettings);
            EditorUtility.SetDirty(manager);
            Debug.Log($"XR Management ready for {group} with OpenXR.");
        }

        EditorUtility.SetDirty(settings);
        AssetDatabase.SaveAssets();
    }

    static void ConfigureOpenXr()
    {
        foreach (var group in XRBuildTargets)
        {
            FeatureHelpers.RefreshFeatures(group);

            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(group);
            if (settings == null)
                throw new InvalidOperationException($"OpenXR settings were not created for {group}.");

            settings.renderMode = OpenXRSettings.RenderMode.SinglePassInstanced;
            settings.optimizeBufferDiscards = true;

            EnableFeature<OculusTouchControllerProfile>(settings, group);

            if (group == BuildTargetGroup.Android)
            {
                EnableFeature<MetaQuestTouchProControllerProfile>(settings, group);
                var metaQuest = EnableFeature<MetaQuestFeature>(settings, group);
                metaQuest.AddTargetDevice("quest3", "Quest 3", true);
                metaQuest.AddTargetDevice("quest3s", "Quest 3S", true);
                EditorUtility.SetDirty(metaQuest);
            }

            EditorUtility.SetDirty(settings);
            Debug.Log($"OpenXR ready for {group}: Single Pass Instanced, Oculus Touch profile enabled.");
        }

        AssetDatabase.SaveAssets();
    }

    static T EnableFeature<T>(OpenXRSettings settings, BuildTargetGroup group) where T : UnityEngine.XR.OpenXR.Features.OpenXRFeature
    {
        var feature = settings.GetFeature<T>();
        if (feature == null)
            throw new InvalidOperationException($"OpenXR feature {typeof(T).Name} was not found for {group}.");

        feature.enabled = true;
        EditorUtility.SetDirty(feature);
        return feature;
    }

    static void BuildScene()
    {
        var rigPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RigPrefabPath);
        if (rigPrefab == null)
            throw new FileNotFoundException($"XR Origin prefab not found at {RigPrefabPath}. Import the XRI Starter Assets sample first.");

        EnsureFolder("Assets/Scenes");

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        var floorMaterial = GetOrCreateMaterial("Floor", new Color(0.16f, 0.18f, 0.22f), 0.15f);
        var tableMaterial = GetOrCreateMaterial("Table", new Color(0.32f, 0.22f, 0.15f), 0.25f);
        var cubeMaterial = GetOrCreateMaterial("Interactable", new Color(0.08f, 0.55f, 0.95f), 0.45f);

        var environment = new GameObject("Environment").transform;

        var floor = CreatePrimitive(
            "Floor",
            PrimitiveType.Cube,
            new Vector3(0f, -0.05f, 0f),
            new Vector3(10f, 0.1f, 10f),
            floorMaterial,
            environment);

        var teleportArea = floor.AddComponent<TeleportationArea>();
        teleportArea.colliders.Clear();
        teleportArea.colliders.Add(floor.GetComponent<Collider>());

        CreatePrimitive(
            "Table",
            PrimitiveType.Cube,
            new Vector3(0f, 0.4f, 1.6f),
            new Vector3(1.4f, 0.8f, 0.9f),
            tableMaterial,
            environment);

        CreatePrimitive(
            "Table Top",
            PrimitiveType.Cube,
            new Vector3(0f, 0.82f, 1.6f),
            new Vector3(1.55f, 0.04f, 1.05f),
            tableMaterial,
            environment);

        for (var i = -1; i <= 1; i++)
        {
            var cube = CreatePrimitive(
                $"Grabbable Cube {i + 2}",
                PrimitiveType.Cube,
                new Vector3(i * 0.34f, 0.95f, 1.6f),
                Vector3.one * 0.22f,
                cubeMaterial,
                environment);

            var rigidbody = cube.AddComponent<Rigidbody>();
            rigidbody.mass = 0.5f;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;
            rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            cube.AddComponent<XRGrabInteractable>();
        }

        var lightObject = new GameObject("Directional Light");
        lightObject.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        var light = lightObject.AddComponent<Light>();
        light.type = LightType.Directional;
        light.color = new Color(1f, 0.96f, 0.9f);
        light.intensity = 1.2f;
        light.shadows = LightShadows.Soft;
        RenderSettings.sun = light;
        RenderSettings.ambientMode = AmbientMode.Trilight;
        RenderSettings.ambientSkyColor = new Color(0.35f, 0.4f, 0.5f);
        RenderSettings.ambientEquatorColor = new Color(0.2f, 0.22f, 0.25f);
        RenderSettings.ambientGroundColor = new Color(0.08f, 0.08f, 0.08f);

        var rig = (GameObject)PrefabUtility.InstantiatePrefab(rigPrefab);
        rig.name = "XR Origin (XR Rig)";
        rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

        var interactionManager = new GameObject("XR Interaction Manager");
        interactionManager.AddComponent<XRInteractionManager>();

        var eventSystem = new GameObject("EventSystem");
        eventSystem.AddComponent<EventSystem>();
        var inputModule = eventSystem.AddComponent<XRUIInputModule>();
        inputModule.enableXRInput = true;
        inputModule.enableMouseInput = true;

        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);

        Debug.Log($"Rebuilt {ScenePath} with XR Origin, interaction manager, teleport floor, and grabbable cubes.");
    }

    static GameObject CreatePrimitive(
        string name,
        PrimitiveType type,
        Vector3 position,
        Vector3 scale,
        Material material,
        Transform parent)
    {
        var gameObject = GameObject.CreatePrimitive(type);
        gameObject.name = name;
        gameObject.transform.SetParent(parent, false);
        gameObject.transform.localPosition = position;
        gameObject.transform.localScale = scale;
        gameObject.GetComponent<Renderer>().sharedMaterial = material;
        return gameObject;
    }

    static Material GetOrCreateMaterial(string name, Color color, float smoothness)
    {
        EnsureFolder(MaterialFolder);
        var path = $"{MaterialFolder}/{name}.mat";
        var material = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (material == null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
            if (shader == null)
                throw new InvalidOperationException("Could not find a suitable lit shader for the VR sample scene.");

            material = new Material(shader);
            AssetDatabase.CreateAsset(material, path);
        }

        if (material.HasProperty("_BaseColor"))
            material.SetColor("_BaseColor", color);
        if (material.HasProperty("_Color"))
            material.SetColor("_Color", color);
        if (material.HasProperty("_Smoothness"))
            material.SetFloat("_Smoothness", smoothness);
        if (material.HasProperty("_Glossiness"))
            material.SetFloat("_Glossiness", smoothness);

        EditorUtility.SetDirty(material);
        return material;
    }

    static void EnsureFolder(string path)
    {
        if (AssetDatabase.IsValidFolder(path))
            return;

        var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name))
            throw new ArgumentException($"Invalid asset folder path: {path}");

        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, name);
    }
}
