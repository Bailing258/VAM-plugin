using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Runtime.InteropServices;
using BepInEx;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Networking;
using SimpleJSON;
using ICSharpCode.SharpZipLib.Zip;
using MVR.FileManagement;
using Valve.VR;
using HarmonyLib;

[BepInPlugin("local.vam.allpackageslinker", "AllPackagesLinker", "1.4.1")]
public partial class AllPackagesLinkerBepInEx : BaseUnityPlugin {
    private const string PluginVersion = "1.4.1";
    private const string TimelineConverterVersion = "timeline-optimized-v1";
    private const long MaxLargeSceneTextBytes = 1024L * 1024L * 1024L;
    private const string LinkRootName = "_AllPackagesLinkerLinks";
    private const string CacheHeader = "#APL_INDEX_V2";
    private const string ListSep = "\u001f";
    private const string MissingDepsDownloadRootDefault = @"E:\VAM";
    private const string HubApiUrl = "https://hub.virtamate.com/citizenx/api.php";
    private const int MaxPendingVrOpenRetries = 40;
    private const int MaxScenePrewarmTextures = 32;
    private const float SceneLoadDispatchDelay = 0.10f;
    private const float SceneLoadProfilePollInterval = 0.25f;
    private const float SceneLoadProfileStableSeconds = 2.0f;
    private const float SceneLoadProfileNoLoadingGraceSeconds = 5.0f;
    private const float SceneLoadProfileTimeoutSeconds = 180.0f;
    private const string TextureFinishHarmonyId = "local.vam.allpackageslinker.texture-finish";
    private const string AssetCallbackHarmonyId = "local.vam.allpackageslinker.asset-callback";
    private const string LazyCuaHarmonyId = "local.vam.allpackageslinker.lazy-cua";
    private const int TextureFinishVanillaLimit = 4;
    private const int SYMBOLIC_LINK_FLAG_FILE = 0x0;
    private const int SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE = 0x2;

    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    private static extern bool CreateSymbolicLink(string lpSymlinkFileName, string lpTargetFileName, int dwFlags);

    private class PackageLite {
        public string uid="", fullPath="", relPath="", description="", thumbEntry="", thumbCache="", firstScene="";
        public long size, mtimeUtcTicks;
        public List<string> cats = new List<string>();
        public List<string> deps = new List<string>();
        public List<string> scenes = new List<string>();
        public List<string> presetSpecs = new List<string>();
        public string CatText { get { return cats.Count == 0 ? "Other" : string.Join(", ", cats.ToArray()); } }
    }

    private class DirItem {
        public string dir; public bool via;
        public DirItem(string d, bool v) { dir=d; via=v; }
    }

    private class LinkResult {
        public int created=0, already=0;
        public List<string> missing = new List<string>();
        public List<string> errors = new List<string>();
    }

    private class HubDownloadInfo {
        public string requestName="", filename="", downloadUrl="", resourceId="", fileSize="";
    }

    private class DepCheckResult {
        public List<string> haveAddon = new List<string>();
        public List<string> haveLibrary = new List<string>();
        public List<string> missing = new List<string>();
    }

    private class PresetLite {
        public string name="", fullPath="", relPath="", presetType="Appearance";
        public long size, mtimeUtcTicks;
    }

    private class VarPresetLite {
        public PackageLite package;
        public string entryPath="", name="", presetType="Appearance";
    }

    private class VarPresetThumbJob {
        public VarPresetLite preset;
        public Image image;
        public Text iconText;
    }

    private class SceneScriptRefOccurrence {
        public int start, length;
        public string fullRef="", uid="", scriptSuffix="";
    }

    private class SceneAtomSpan {
        public int start, length;
        public string id="", type="";
    }

    private class SceneJsonAnalysis {
        public string key="", json="", error="";
        public int atomsOpen=-1, atomsClose=-1;
        public List<SceneAtomSpan> atoms = new List<SceneAtomSpan>();
        public List<string> personIds = new List<string>();
    }

    private class SceneVariantResult {
        public string primaryJson="", deferredJson="";
        public int totalAtoms=0, keptAtoms=0, deferredAtoms=0;
        public List<string> deferredTypes = new List<string>();
    }

    private class TimelinePropertySpan {
        public string key="";
        public int start, length;
        public char kind;
    }

    private class TimelineObjectSpan {
        public int start, length;
    }

    private class TimelineRewriteSpan {
        public int start, length;
        public bool animationHeader;
    }

    private class TimelineOptimizationInfo {
        public bool cacheHit, optimized;
        public int animations, curves;
        public long keyframes, sourceBytes, outputBytes;
        public double readMs, optimizeMs, cacheReadMs;
        public string cachePath="", error="";
    }

    private class CacheUsageSnapshot {
        public long nonEssentialBytes, allBytes;
        public int nonEssentialFiles, allFiles, errors;
    }

    private class CacheWorkerResult {
        public string operation="", error="";
        public CacheUsageSnapshot usage;
        public long deletedBytes;
        public int deletedFiles, deleteErrors;
    }

    private class CacheDeleteReport {
        public long deletedBytes;
        public int deletedFiles, errors;
    }

    private class SceneLite {
        public PackageLite package;
        public string entryPath="", name="";
    }
    private class WearableLite {
        public PackageLite package;
        public string entryPath="", previewEntry="", name="", wearableType="Clothing";
    }
    private class PresetLinkDiag {
        public int linked=0, already=0;
        public List<string> missing = new List<string>();
        public List<string> errors = new List<string>();
    }
    private delegate void UiAction();

    private string vamRoot, allRoot, addonRoot, linkRoot, dataRoot, thumbRoot, indexPath, configPath, favoritesPath, favoriteUidsPath, defaultsPath, logPath, debugLogPath;
    private List<PackageLite> all = new List<PackageLite>();
    private List<PresetLite> localPresets = new List<PresetLite>();
    private List<VarPresetLite> varPresets = new List<VarPresetLite>();
    private List<SceneLite> sceneItems = new List<SceneLite>();
    private List<WearableLite> wearableItems = new List<WearableLite>();
    private bool wearableIndexBuilt = false;
    private HashSet<string> favoritePresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private string favoritePresetsPath;
    private Dictionary<string, PackageLite> allExact = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PackageLite> allLatest = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PackageLite> addonExact = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PackageLite> addonLatest = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> materializedScriptRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private Canvas canvas; private GameObject root, confirmRoot, subBarRoot, pageStripRoot, authorDropdownRoot; private Transform listContent, authorDropdownContent; private Text header, details, statusText, downloadProgressText; private Image preview, downloadProgressFill;
    private Sprite previewSprite; private Texture2D previewTex; private Font font;
    private List<Sprite> listThumbSprites = new List<Sprite>();
    private List<Texture2D> listThumbTextures = new List<Texture2D>();
    private Coroutine thumbLoadCoroutine = null;
    private List<Image> tabBgs = new List<Image>();
    private string[] tabCats;
    private string searchQuery = "";
    // "All" is the sentinel.  A VAR package uid is normally Author.Package.Version.
    private string authorFilter = "All";
    private InputField searchInput;
    private InputField authorDropdownSearchInput;
    // UI refactor refs (plan.md)
    private GameObject navRoot, settingsDrawerRoot, settingsBackdropRoot, emptyStateRoot;
    private GameObject atomRowRoot, presetOptionsRoot, presetModeRoot, presetActionRoot, sceneModeRoot, scenePersonRoot, sceneActionRoot, linkActionRoot, hubRowRoot, hubDownloadRoot, progressSectionRoot, dangerRowRoot, moreActionsRoot;
    private Text resultCountText, pageInfoText, searchPlaceholderText, cacheSizeText;
    private Button settingsBtn, rescanTopBtn, searchClearBtn, clearNonEssentialCacheBtn, clearAllCacheBtn;
    private Button loadSceneBtn, loadDeferredSceneBtn, sceneFullModeBtn, scenePrimaryModeBtn, sceneMinimalModeBtn, applyPresetBtn, loadScriptBtn, linkOnlyBtn, defaultKeepBtn, favToggleBtn;
    private bool settingsDrawerOpen = false;
    private volatile bool cacheWorkerRunning = false;
    private volatile CacheWorkerResult cacheWorkerResult = null;
    private CacheUsageSnapshot lastCacheUsage = null;
    private string allSubFilter = "All";
    private string targetAtomUid = "";
    private Text atomSelectorLabel;
    private Text scenePrimaryPersonLabel;
    private bool applyClothing = true;
    // 人物外观预设默认带头发；仅当用户显式关闭“包含头发”时才锁定头发
    private bool applyHair = true;
    private Toggle applyClothingToggle, applyHairToggle;
    private HashSet<string> favoriteUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> favoriteScenes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> defaultUids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool scanned=false, scanning=false, lastCombo=false, updateLoopSeen=false; private int page=0, pageSize=64; private string activeCat="Scenes", status="", pendingScenePath=""; private PackageLite selected;
    private int pendingSceneExpectedAtomCount = 0;
    private bool sceneLoadProfileActive = false;
    private bool sceneLoadProfileSeenLoading = false;
    private bool sceneLoadProfileLastLoading = false;
    private float sceneLoadProfileStartedAt = 0f;
    private float sceneLoadProfileNextPollAt = 0f;
    private float sceneLoadProfileLastChangeAt = 0f;
    private int sceneLoadProfileExpectedAtoms = 0;
    private int sceneLoadProfileLastAtomCount = -1;
    private int sceneLoadProfileChangeEvents = 0;
    private string sceneLoadProfilePath = "";
    private string sceneLoadProfileLastAdded = "";
    private HashSet<string> sceneLoadProfileAtoms = new HashSet<string>(StringComparer.Ordinal);
    private bool sceneLoadProfileHoldsInitialized = false;
    private int sceneLoadProfileMaxPendingHolds = 0;
    private float sceneLoadProfileLongestHoldSeconds = 0f;
    private string sceneLoadProfileLongestHold = "";
    private string sceneLoadProfileLastCompletedHold = "";
    private HashSet<AsyncFlag> sceneLoadProfilePendingHolds = new HashSet<AsyncFlag>();
    private Dictionary<AsyncFlag, float> sceneLoadProfileHoldFirstSeen = new Dictionary<AsyncFlag, float>();
    private Dictionary<AsyncFlag, string> sceneLoadProfileHoldLabels = new Dictionary<AsyncFlag, string>();
    private FieldInfo sceneHoldLoadCompleteFlagsField = null;
    private FieldInfo sceneCuaLoadingFlagField = null;
    private FieldInfo sceneCuaAssetUrlField = null;
    private FieldInfo sceneCuaResolvedUrlField = null;
    private string favSubCat = "All"; // All, Scenes, Looks, Scripts, Presets
    private Dictionary<string, int> tabPages = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    private List<Button> favSubBtns = new List<Button>();
    private float nextHeartbeat=0f;
    private float lastLoadClickAt=-10f;
    private float uiScale = 0.00082f;
    private float uiDistance = 1.20f;
    private float uiYOffset = -0.04f;
    private bool autoOpenPanelInEditMode = false;
    private bool autoOpenPanelOnPluginLoad = false;
    // This is deliberately separate from the option above: that option opens this
    // plugin's library UI when BepInEx loads, while this one opens VaM's native
    // target-atom Plugins UI after a script has been added to an atom.
    private bool autoOpenTargetAtomPluginPanel = true;
    private int autoOpenRetryCount = 0;
    private const int MaxAutoOpenRetries = 20;
    private string pendingPluginPanelAtomUid = "";
    private string pendingPluginPanelSlotUid = "";
    private int pendingPluginPanelRetryCount = 0;
    private const int MaxPendingPluginPanelRetries = 30;
    private bool autoAllowAllPlugins = false;
    // VR look mode (Left Stick Click toggle): stick X = yaw, stick Y = height
    private bool vrRotationEnabled = true;
    private float vrRotationSensitivity = 60f;
    private float vrHeightSpeed = 0.90f; // meters/sec when stick fully pushed
    private float vrRotationDeadzone = 0.18f;
    private bool vrRotationInvert = false;
    private bool vrHeightInvert = false;
    private float vrRotationSnapAngle = 0f;
    private float vrRotationSmoothing = 0.10f;
    private bool scanAllPackagesOnStartup = false;
    private bool autoCleanLinksBeforeSceneLoad = false;
    private bool sceneTexturePrewarmEnabled = true;
    private bool lazyDisabledCuaEnabled = true;
    private int textureFinishGear = 1; // 0=vanilla 4, 1=balanced 8, 2=fast 12, 3=extreme 16
    private int assetCallbackGear = 2; // 0=ordered, 1=2/frame, 2=4/frame, 3=8/frame
    private Harmony textureFinishHarmony = null;
    private static AllPackagesLinkerBepInEx textureFinishOwner = null;
    private static bool textureFinishPatchApplied = false;
    private static int textureFinishTranspilerHits = 0;
    private Harmony assetCallbackHarmony = null;
    private static AllPackagesLinkerBepInEx assetCallbackOwner = null;
    private static bool assetCallbackPatchApplied = false;
    private static int assetWorkerTranspilerHits = 0;
    private static FieldInfo assetCompletionQueueField = null;
    private static FieldInfo assetRequestLoadCompletedField = null;
    private Harmony lazyCuaHarmony = null;
    private static AllPackagesLinkerBepInEx lazyCuaOwner = null;
    private static bool lazyCuaPatchApplied = false;
    private static MethodInfo lazyCuaSyncAssetUrlMethod = null;
    private HashSet<string> lazyCuaCandidateAtomUids = new HashSet<string>(StringComparer.Ordinal);
    private Dictionary<CustomUnityAssetLoader, string> deferredCuaUrls = new Dictionary<CustomUnityAssetLoader, string>();
    private HashSet<CustomUnityAssetLoader> activatingDeferredCuaLoads = new HashSet<CustomUnityAssetLoader>();
    private float nextDeferredCuaPollAt = 0f;
    private int sceneLoadProfileDeferredCua = 0;
    private int sceneLoadProfileActivatedDeferredCua = 0;
    private int sceneLoadProfileAssetCallbacks = 0;
    private int sceneLoadProfileOutOfOrderCallbacks = 0;
    private int sceneLoadProfileMaxCallbackScanAhead = 0;
    private double sceneLoadProfileAssetCallbackWorkMs = 0.0;
    private double sceneLoadProfileSlowestAssetCallbackMs = 0.0;
    private string sceneLoadProfileSlowestAssetCallback = "";
    private int sceneLoadMode = 0; // 0=full, 1=primary, 2=minimal
    private string scenePrimaryPersonId = "";
    private SceneJsonAnalysis selectedSceneAnalysis = null;
    private Coroutine scenePrewarmCoroutine = null;
    private int scenePrewarmGeneration = 0;
    private int scenePrewarmPending = 0;
    private int scenePrewarmErrors = 0;
    private string scenePrewarmKey = "";
    private HashSet<string> activePrewarmSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private List<ImageLoaderThreaded.QueuedImage> activePrewarmImages = new List<ImageLoaderThreaded.QueuedImage>();
    private string pendingDeferredScenePath = "";
    private int pendingDeferredAtomCount = 0;
    private float scenePrewarmWaitUntil = 0f;
    private string missingDepsDownloadRoot = MissingDepsDownloadRootDefault;
    private string configuredDownloadRoot = MissingDepsDownloadRootDefault;
    private bool missingDepsDownloadRunning = false;
    private bool missingDepsDownloadCancelRequested = false;
    private Button hubDownloadButton = null;
    private Process activeHubProcess = null;
    private float downloadProgressValue = 0f;
    private string downloadProgressLabel = "尚未开始下载";
    private int pageSizeBeforeVr = 0;
    private PackageLite lastDepsCheckedPackage = null;
    private string lastSteamVRDiag = "";
    private string lastOpenVRDiag = "";
    private float nextSteamVRQuietDiagAt = 0f;
    private float nextOpenVRQuietDiagAt = 0f;
    private bool isVRMode = false;
    private bool openedViaVR = false;
    private int pendingVrOpenRetries = 0;
    private bool vrCanvasWaitingForPlacement = false;
    private float nextVrPlacementRetryAt = 0f;
    // SteamVR action state can briefly alternate while one gesture is held.
    // Latch the toggle until every controller button is released so the menu
    // cannot close/open again during a single physical gesture.
    private bool vrComboLatched = false;
    private bool vrRotationModeActive = false;
    private bool leftStickClickHeldLastFrame = false;
    private float vrRotationFilteredX = 0f;
    private float vrRotationFilteredY = 0f;
    private float pendingVrYaw = 0f;
    private float pendingVrHeight = 0f;
    private bool vrSnapArmed = true;
    private string lastVrRotationDiag = "";
    private float nextVrRotationQuietDiagAt = 0f;

    // Clean high-contrast dark theme (desktop + VR readable)
    private static readonly Color colBg = new Color(0.14f, 0.16f, 0.20f, 0.96f);
    private static readonly Color colPanel = new Color(0.18f, 0.20f, 0.26f, 0.96f);
    private static readonly Color colCard = new Color(0.22f, 0.24f, 0.30f, 0.96f);
    private static readonly Color colCardHover = new Color(0.28f, 0.32f, 0.40f, 0.98f);
    private static readonly Color colCardSelected = new Color(0.22f, 0.38f, 0.62f, 0.98f);
    private static readonly Color colAccent = new Color(0.30f, 0.62f, 1.00f, 1f);
    private static readonly Color colAccentDim = new Color(0.24f, 0.48f, 0.86f, 0.95f);
    private static readonly Color colBtn = new Color(0.28f, 0.32f, 0.40f, 0.98f);
    private static readonly Color colBtnHover = new Color(0.34f, 0.40f, 0.50f, 1f);
    private static readonly Color colTextPrimary = new Color(0.96f, 0.97f, 0.99f, 1f);
    private static readonly Color colTextSecondary = new Color(0.78f, 0.82f, 0.88f, 1f);
    private static readonly Color colTextDim = new Color(0.58f, 0.62f, 0.70f, 1f);
    private static readonly Color colDivider = new Color(0.35f, 0.38f, 0.46f, 0.70f);
    private static readonly Color colScrollBg = new Color(0.12f, 0.14f, 0.18f, 0.96f);
    private static readonly Color colThumbBg = new Color(0.16f, 0.18f, 0.22f, 0.95f);
    private static readonly Color colDanger = new Color(0.82f, 0.28f, 0.28f, 0.95f);
    private static readonly Color colSuccess = new Color(0.20f, 0.62f, 0.40f, 0.95f);

    private void Awake() {
        try {
            Logger.LogInfo("AllPackagesLinker Awake begin. version=" + PluginVersion);
            vamRoot = Directory.GetParent(Application.dataPath).FullName;
            allRoot = Path.Combine(vamRoot, "Allpackages");
            addonRoot = Path.Combine(vamRoot, "AddonPackages");
            linkRoot = Path.Combine(addonRoot, LinkRootName);
            dataRoot = Path.Combine(vamRoot, "Saves\\PluginData\\AllPackagesLinker");
            thumbRoot = Path.Combine(dataRoot, "thumbs");
            indexPath = Path.Combine(dataRoot, "index.tsv");
            configPath = Path.Combine(dataRoot, "config.tsv");
            favoritesPath = Path.Combine(dataRoot, "favorites.txt");
            favoriteUidsPath = Path.Combine(dataRoot, "favorite_uids.txt");
            favoritePresetsPath = Path.Combine(dataRoot, "favorite_presets.txt");
            defaultsPath = Path.Combine(dataRoot, "defaults.txt");
            logPath = Path.Combine(dataRoot, "bepinex.log");
            debugLogPath = Path.Combine(dataRoot, "debug.log");
            Directory.CreateDirectory(allRoot); Directory.CreateDirectory(dataRoot); Directory.CreateDirectory(thumbRoot);
            DebugLog("Awake paths ready. version=" + PluginVersion + ", vamRoot=" + vamRoot + ", allRoot=" + allRoot + ", addonRoot=" + addonRoot);
            CleanBrokenGeneratedLinks();
            try { font = Font.CreateDynamicFontFromOSFont(new string[]{"Microsoft YaHei","SimHei","Arial"}, 16); } catch {}
            if(font==null) font = (Font)Resources.GetBuiltinResource(typeof(Font), "Arial.ttf");
            DebugLog("Builtin font loaded=" + (font != null));
            LoadConfig();
            InstallTextureFinishPatch();
            InstallAssetCallbackPatch();
            InstallLazyCuaPatch();
            configuredDownloadRoot = missingDepsDownloadRoot;
            string linkedDownloadRoot = ResolveLinkedLibraryDownloadRoot(missingDepsDownloadRoot);
            if (!string.Equals(linkedDownloadRoot, missingDepsDownloadRoot, StringComparison.OrdinalIgnoreCase)) {
                missingDepsDownloadRoot = linkedDownloadRoot;
                SaveConfig();
                DebugLog("Hub download root redirected through Allpackages link: configured=" + configuredDownloadRoot + ", active=" + missingDepsDownloadRoot);
            }
            LoadMarks();
            int cached = LoadCacheIntoMemory();
            DebugLog("Cache loaded. cachedPackages=" + cached + ", indexPath=" + indexPath + ", indexExists=" + File.Exists(indexPath));
            ScanAddonLightweight();
            ScanLocalPresets();
            EnsureVarPresetIndex();
            EnsureSceneIndex();
            DebugLog("Startup preset dependency prelink skipped; dependencies are linked on demand when a preset is applied.");
            CancelInvoke("PatchLoadedPackageScriptPluginUrls");
            Invoke("PatchLoadedPackageScriptPluginUrls", 4.0f);
            Invoke("PatchLoadedPackageScriptPluginUrls", 8.0f);
            if (scanAllPackagesOnStartup) {
                SetStatus("已加载缓存：" + cached + " 个包，" + localPresets.Count + " 个本地预设。启动后会增量检查新增/变化的 .var。", true);
                Invoke("DelayedStartupIncrementalScan", 1.0f);
            } else {
                SetStatus("已加载缓存：" + cached + " 个包，" + localPresets.Count + " 个本地预设。已跳过启动全库扫描，需要更新库时点“重新扫描”。", true);
                DebugLog("Startup full scan skipped by config.");
            }
            autoOpenRetryCount = 0;
            Invoke("TryAutoOpenPanelOnPluginLoad", 2.0f);
            Invoke("TryAutoOpenPanelInEditMode", 2.5f);
            DebugLog("Awake end. Delayed startup scan scheduled. autoOpenOnLoad=" + autoOpenPanelOnPluginLoad + ", vrRotation=" + vrRotationEnabled);
        } catch(Exception e) {
            DebugLog("Awake FAILED: " + e.ToString());
            Logger.LogError(e);
        }
    }

    private void RefreshVamAfterStartupPrelink() {
        try {
            DebugLog("RefreshVamAfterStartupPrelink begin.");
            RefreshVam();
            DebugLog("RefreshVamAfterStartupPrelink end. addonExact=" + addonExact.Count + ", addonLatest=" + addonLatest.Count);
        } catch(Exception e) {
            DebugLog("RefreshVamAfterStartupPrelink failed: " + e.Message);
        }
    }

    private void DelayedStartupIncrementalScan() { DebugLog("DelayedStartupIncrementalScan begin."); ScanPackages(); if (canvas != null) RefreshList(); DebugLog("DelayedStartupIncrementalScan end. scanned=" + scanned + ", allCount=" + all.Count); }

    private void Update() {
        try {
            if (!updateLoopSeen) {
                updateLoopSeen = true;
                DebugLog("Update loop alive. Hotkeys: F8/F7, Unity Joy14/Joy4+A/Joy8+A, SteamVR left HoldGrab/Menu/Joystick + right A(Select/UIInteract), OpenVR raw left Grip/Menu/Stick + right A/Menu.");
            }
            if (Time.realtimeSinceStartup >= nextHeartbeat) {
                nextHeartbeat = Time.realtimeSinceStartup + 10f;
                DebugLog("Heartbeat. t=" + Time.realtimeSinceStartup.ToString("0.0") + ", canvas=" + (canvas != null) + ", scanned=" + scanned + ", scanning=" + scanning + ", packages=" + all.Count + ", superController=" + (SuperController.singleton != null) + ", unity=" + PressedJoystickButtons() + ", steamvr=" + OneLine(lastSteamVRDiag, 160) + ", openvr=" + OneLine(lastOpenVRDiag, 160));
            }
            PollSceneLoadProfile(false);
            PollDeferredCuaLoads();
            PollCacheWorkerResult();
            if (canvas != null && isVRMode && vrCanvasWaitingForPlacement && Time.realtimeSinceStartup >= nextVrPlacementRetryAt) {
                nextVrPlacementRetryAt = Time.realtimeSinceStartup + 0.5f;
                if (CanPlaceVrCanvas()) {
                    DebugLog("VR view became ready; recentering existing canvas.");
                    ApplyCanvasTransform();
                }
            }
            bool f8 = Input.GetKeyDown(KeyCode.F8);
            bool f7 = Input.GetKeyDown(KeyCode.F7);
            if (f8 || f7) {
                DebugLog("Keyboard hotkey detected. F8=" + f8 + ", F7=" + f7);
                openedViaVR = false;
                TogglePanel();
            }
            LogJoystickButtonDowns();
            bool aButton = Input.GetKey(KeyCode.JoystickButton0) || Input.GetKey(KeyCode.JoystickButton1);
            bool leftGripSide = Input.GetKey(KeyCode.JoystickButton4);
            bool legacyLeftStick = Input.GetKey(KeyCode.JoystickButton8);
            bool observedVirtualDesktopSideHeld = Input.GetKey(KeyCode.JoystickButton14);
            bool observedVirtualDesktopSideDown = Input.GetKeyDown(KeyCode.JoystickButton14);
            bool unityHoldCombo = (leftGripSide && aButton) || (legacyLeftStick && aButton);
            bool unityCombo = observedVirtualDesktopSideDown || unityHoldCombo;
            string steamDiag, openvrDiag;
            bool steamCombo = SteamVRCombo(out steamDiag);
            bool openvrCombo = OpenVRRawCombo(out openvrDiag);
            bool combo = unityCombo || steamCombo || openvrCombo;
            bool anyControllerButton = PressedJoystickButtons() != "-" || HasActiveVrButton(steamDiag, openvrDiag);
            if (!anyControllerButton) vrComboLatched = false;
            if (combo && !vrComboLatched) {
                DebugLog("VR hotkey detected. unityCombo=" + unityCombo + " {Joy14Down=" + observedVirtualDesktopSideDown + ", Joy14Held=" + observedVirtualDesktopSideHeld + ", Joy4=" + leftGripSide + ", Joy8=" + legacyLeftStick + ", A(Joy0/Joy1)=" + aButton + ", pressed=" + PressedJoystickButtons() + "} steamCombo=" + steamCombo + " {" + steamDiag + "} openvrCombo=" + openvrCombo + " {" + openvrDiag + "}");
                openedViaVR = true;
                vrComboLatched = true;
                TogglePanel();
            }
            lastCombo = combo;
            UpdateVrRotationInput();
        } catch(Exception e) {
            DebugLog("Update FAILED: " + e.ToString());
            Logger.LogError(e);
        }
    }

    private void LateUpdate() {
        try {
            ApplyPendingVrYaw();
        } catch(Exception e) {
            DebugLog("LateUpdate FAILED: " + e.ToString());
        }
    }

    private bool SteamVRCombo(out string diag) {
        diag = "unavailable";
        try {
            bool leftHoldGrab = SteamBool(SteamVR_Actions.default_HoldGrab, SteamVR_Input_Sources.LeftHand);
            bool leftRemoteHoldGrab = SteamBool(SteamVR_Actions.default_RemoteHoldGrab, SteamVR_Input_Sources.LeftHand);
            bool leftMenu = SteamBool(SteamVR_Actions.default_Menu, SteamVR_Input_Sources.LeftHand);
            bool leftGrabNavigate = SteamBool(SteamVR_Actions.default_GrabNavigate, SteamVR_Input_Sources.LeftHand);

            bool rightSelect = SteamBool(SteamVR_Actions.default_Select, SteamVR_Input_Sources.RightHand);
            bool rightUIInteract = SteamBool(SteamVR_Actions.default_UIInteract, SteamVR_Input_Sources.RightHand);
            bool rightMenu = SteamBool(SteamVR_Actions.default_Menu, SteamVR_Input_Sources.RightHand);
            bool anySelect = SteamBool(SteamVR_Actions.default_Select, SteamVR_Input_Sources.Any);
            bool anyUIInteract = SteamBool(SteamVR_Actions.default_UIInteract, SteamVR_Input_Sources.Any);

            bool leftSide = leftHoldGrab || leftRemoteHoldGrab || leftMenu || leftGrabNavigate;
            bool aLike = rightSelect || rightUIInteract || anySelect || anyUIInteract || rightMenu;
            bool combo = leftSide && aLike;

            diag = "LH[HoldGrab=" + B(leftHoldGrab) + ",RemoteHoldGrab=" + B(leftRemoteHoldGrab) + ",Menu=" + B(leftMenu) + ",StickClick=" + B(leftGrabNavigate) + "] RH/A[Select=" + B(rightSelect) + ",UIInteract=" + B(rightUIInteract) + ",Menu/B=" + B(rightMenu) + ",AnySelect=" + B(anySelect) + ",AnyUI=" + B(anyUIInteract) + "]";
            MaybeLogSteamVRDiag(diag, leftSide || aLike || combo);
            return combo;
        } catch(Exception e) {
            diag = "ERR " + e.GetType().Name + ": " + e.Message;
            MaybeLogSteamVRDiag(diag, false);
            return false;
        }
    }

    private bool SteamBool(SteamVR_Action_Boolean action, SteamVR_Input_Sources source) {
        try { return action != null && action.GetState(source); }
        catch { return false; }
    }

    private bool SteamBoolDown(SteamVR_Action_Boolean action, SteamVR_Input_Sources source) {
        try { return action != null && action.GetStateDown(source); }
        catch { return false; }
    }

    private Vector2 SteamAxis(SteamVR_Action_Vector2 action, SteamVR_Input_Sources source) {
        try { return action != null ? action.GetAxis(source) : Vector2.zero; }
        catch { return Vector2.zero; }
    }

    private bool HasActiveVrButton(string steamDiag, string openvrDiag) {
        try {
            if (!string.IsNullOrEmpty(steamDiag)) {
                string[] active = new string[]{"HoldGrab=1","RemoteHoldGrab=1","Menu=1","StickClick=1","Select=1","UIInteract=1","AnySelect=1","AnyUI=1"};
                for (int i=0;i<active.Length;i++) if (steamDiag.IndexOf(active[i], StringComparison.Ordinal) >= 0) return true;
            }
            if (!string.IsNullOrEmpty(openvrDiag)) {
                string[] active = new string[]{"Grip=1","Menu=1","A=1","Axis0-4=10000","Axis0-4=01000","Axis0-4=00100","Axis0-4=00010","Axis0-4=00001"};
                for (int i=0;i<active.Length;i++) if (openvrDiag.IndexOf(active[i], StringComparison.Ordinal) >= 0) return true;
            }
        } catch {}
        return false;
    }

    private void MaybeLogSteamVRDiag(string diag, bool active) {
        try {
            if (diag != lastSteamVRDiag) {
                lastSteamVRDiag = diag;
                if (active || Time.realtimeSinceStartup >= nextSteamVRQuietDiagAt) {
                    nextSteamVRQuietDiagAt = Time.realtimeSinceStartup + 10f;
                    DebugLog("SteamVR input state: " + diag);
                }
            }
        } catch {}
    }

    private bool OpenVRRawCombo(out string diag) {
        diag = "unavailable";
        try {
            CVRSystem sys = OpenVR.System;
            if (sys == null) {
                diag = "OpenVR.System=null";
                MaybeLogOpenVRDiag(diag, false);
                return false;
            }
            uint left = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
            uint right = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
            bool leftOk = false, rightOk = false;
            ulong lm = 0, rm = 0;
            if (left != OpenVR.k_unTrackedDeviceIndexInvalid) {
                VRControllerState_t ls = new VRControllerState_t();
                leftOk = sys.GetControllerState(left, ref ls, (uint)Marshal.SizeOf(typeof(VRControllerState_t)));
                if (leftOk) lm = ls.ulButtonPressed;
            }
            if (right != OpenVR.k_unTrackedDeviceIndexInvalid) {
                VRControllerState_t rs = new VRControllerState_t();
                rightOk = sys.GetControllerState(right, ref rs, (uint)Marshal.SizeOf(typeof(VRControllerState_t)));
                if (rightOk) rm = rs.ulButtonPressed;
            }

            bool leftGrip = RawButton(lm, EVRButtonId.k_EButton_Grip);
            bool leftMenu = RawButton(lm, EVRButtonId.k_EButton_ApplicationMenu);
            bool leftA = RawButton(lm, EVRButtonId.k_EButton_A);
            bool leftAxis0 = RawButton(lm, EVRButtonId.k_EButton_Axis0);
            bool leftAxis1 = RawButton(lm, EVRButtonId.k_EButton_Axis1);
            bool leftAxis2 = RawButton(lm, EVRButtonId.k_EButton_Axis2);
            bool leftAxis3 = RawButton(lm, EVRButtonId.k_EButton_Axis3);
            bool leftAxis4 = RawButton(lm, EVRButtonId.k_EButton_Axis4);

            bool rightGrip = RawButton(rm, EVRButtonId.k_EButton_Grip);
            bool rightMenu = RawButton(rm, EVRButtonId.k_EButton_ApplicationMenu);
            bool rightA = RawButton(rm, EVRButtonId.k_EButton_A);
            bool rightAxis0 = RawButton(rm, EVRButtonId.k_EButton_Axis0);
            bool rightAxis1 = RawButton(rm, EVRButtonId.k_EButton_Axis1);
            bool rightAxis2 = RawButton(rm, EVRButtonId.k_EButton_Axis2);
            bool rightAxis3 = RawButton(rm, EVRButtonId.k_EButton_Axis3);
            bool rightAxis4 = RawButton(rm, EVRButtonId.k_EButton_Axis4);

            bool leftSide = leftGrip || leftMenu || leftA || leftAxis0 || leftAxis1 || leftAxis2 || leftAxis3 || leftAxis4;
            bool aLike = rightA || rightMenu || rightGrip || rightAxis0 || rightAxis1 || rightAxis2 || rightAxis3 || rightAxis4;
            bool combo = leftSide && aLike;

            diag = "L(idx=" + left + ",ok=" + leftOk + ",mask=0x" + lm.ToString("X") + ",Grip=" + B(leftGrip) + ",Menu=" + B(leftMenu) + ",A=" + B(leftA) + ",Axis0-4=" + B(leftAxis0)+B(leftAxis1)+B(leftAxis2)+B(leftAxis3)+B(leftAxis4) + ") R(idx=" + right + ",ok=" + rightOk + ",mask=0x" + rm.ToString("X") + ",A=" + B(rightA) + ",Menu/B=" + B(rightMenu) + ",Grip=" + B(rightGrip) + ",Axis0-4=" + B(rightAxis0)+B(rightAxis1)+B(rightAxis2)+B(rightAxis3)+B(rightAxis4) + ")";
            MaybeLogOpenVRDiag(diag, lm != 0 || rm != 0 || combo);
            return combo;
        } catch(Exception e) {
            diag = "ERR " + e.GetType().Name + ": " + e.Message;
            MaybeLogOpenVRDiag(diag, false);
            return false;
        }
    }

    private bool RawButton(ulong mask, EVRButtonId id) {
        int bit = (int)id;
        if (bit < 0 || bit >= 64) return false;
        return (mask & (1UL << bit)) != 0;
    }

    private void MaybeLogOpenVRDiag(string diag, bool active) {
        try {
            if (diag != lastOpenVRDiag) {
                lastOpenVRDiag = diag;
                if (active || Time.realtimeSinceStartup >= nextOpenVRQuietDiagAt) {
                    nextOpenVRQuietDiagAt = Time.realtimeSinceStartup + 10f;
                    DebugLog("OpenVR raw state: " + diag);
                }
            }
        } catch {}
    }

    private string B(bool v) { return v ? "1" : "0"; }

    private string PressedJoystickButtons() {
        try {
            List<string> pressed = new List<string>();
            for(int i=0;i<=19;i++) {
                KeyCode kc = (KeyCode)((int)KeyCode.JoystickButton0 + i);
                if(Input.GetKey(kc)) pressed.Add("Joy"+i);
            }
            return pressed.Count==0 ? "-" : string.Join(",", pressed.ToArray());
        } catch { return "?"; }
    }

    private void LogJoystickButtonDowns() {
        try {
            for(int i=0;i<=19;i++) {
                KeyCode kc = (KeyCode)((int)KeyCode.JoystickButton0 + i);
                if(Input.GetKeyDown(kc)) DebugLog("Joystick button down: Joy"+i+" | pressed="+PressedJoystickButtons());
            }
        } catch {}
    }

    private void OnDestroy() {
        DebugLog("OnDestroy.");
        lazyCuaCandidateAtomUids.Clear();
        deferredCuaUrls.Clear();
        activatingDeferredCuaLoads.Clear();
        UninstallLazyCuaPatch();
        UninstallAssetCallbackPatch();
        UninstallTextureFinishPatch();
        missingDepsDownloadCancelRequested=true;
        StopActiveHubProcess();
        SaveMarks();
        StopScenePrewarm(true);
        CancelInvoke("DoDelayedSceneLoad");
        CancelInvoke("TryOpenTargetAtomPluginPanel");
        CancelInvoke("TryDispatchSceneLoadAfterPrewarm");
        ExitVrRotationMode("destroy");
        ClosePanel();
    }

    private void InstallTextureFinishPatch() {
        textureFinishOwner = this;
        textureFinishPatchApplied = false;
        textureFinishTranspilerHits = 0;
        try {
            MethodInfo target = AccessTools.Method(typeof(ImageLoaderThreaded), "PostProcessCompletedImages");
            MethodInfo transpiler = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "TextureFinishTranspiler");
            if (object.ReferenceEquals(target, null) || object.ReferenceEquals(transpiler, null)) throw new MissingMethodException("ImageLoaderThreaded.PostProcessCompletedImages or transpiler not found");
            textureFinishHarmony = new Harmony(TextureFinishHarmonyId);
            textureFinishHarmony.Patch(target, null, null, new HarmonyMethod(transpiler));
            if (textureFinishTranspilerHits != 1) {
                throw new InvalidOperationException("expected one texture finish loop limit, matched " + textureFinishTranspilerHits);
            }
            textureFinishPatchApplied = true;
            DebugLog("Texture finish patch installed. gear=" + TextureFinishGearName(textureFinishGear) + ", loadingLimit=" + TextureFinishLimitForGear(textureFinishGear) + ", idleLimit=" + TextureFinishVanillaLimit);
        } catch(Exception e) {
            try { if (textureFinishHarmony != null) textureFinishHarmony.UnpatchAll(TextureFinishHarmonyId); } catch {}
            textureFinishHarmony = null;
            textureFinishPatchApplied = false;
            Logger.LogWarning("Texture finish acceleration disabled; vanilla limit remains active: " + e.Message);
            DebugLog("Texture finish patch failed: " + e.ToString());
        }
    }

    private void UninstallTextureFinishPatch() {
        try { if (textureFinishHarmony != null) textureFinishHarmony.UnpatchAll(TextureFinishHarmonyId); }
        catch(Exception e) { Logger.LogWarning("Texture finish patch cleanup failed: " + e.Message); }
        textureFinishHarmony = null;
        textureFinishPatchApplied = false;
        if (object.ReferenceEquals(textureFinishOwner, this)) textureFinishOwner = null;
    }

    private static IEnumerable<CodeInstruction> TextureFinishTranspiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
        int hits = 0;
        int matchAt = -1;
        for (int i = 1; i + 1 < codes.Count; i++) {
            bool loopLocal = codes[i - 1].opcode == OpCodes.Ldloc_0;
            bool vanillaLimit = codes[i].opcode == OpCodes.Ldc_I4_4;
            bool loopBranch = codes[i + 1].opcode == OpCodes.Blt || codes[i + 1].opcode == OpCodes.Blt_S;
            if (loopLocal && vanillaLimit && loopBranch) { hits++; matchAt = i; }
        }
        textureFinishTranspilerHits = hits;
        if (hits == 1) {
            MethodInfo limitMethod = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "TextureFinishLimitForCurrentFrame");
            if (!object.ReferenceEquals(limitMethod, null)) {
                codes[matchAt].opcode = OpCodes.Call;
                codes[matchAt].operand = limitMethod;
            } else {
                textureFinishTranspilerHits = 0;
            }
        }
        return codes;
    }

    private static int TextureFinishLimitForCurrentFrame() {
        AllPackagesLinkerBepInEx owner = textureFinishOwner;
        if (!textureFinishPatchApplied || owner == null || owner.textureFinishGear <= 0) return TextureFinishVanillaLimit;
        SuperController sc = SuperController.singleton;
        if (sc == null || !sc.isLoading) return TextureFinishVanillaLimit;
        return TextureFinishLimitForGear(owner.textureFinishGear);
    }

    private static int TextureFinishLimitForGear(int gear) {
        switch (Mathf.Clamp(gear, 0, 3)) {
            case 1: return 8;
            case 2: return 12;
            case 3: return 16;
            default: return TextureFinishVanillaLimit;
        }
    }

    private static string TextureFinishGearName(int gear) {
        switch (Mathf.Clamp(gear, 0, 3)) {
            case 1: return "均衡";
            case 2: return "高速";
            case 3: return "极限";
            default: return "原版";
        }
    }

    private void SetTextureFinishGear(int gear) {
        textureFinishGear = Mathf.Clamp(gear, 0, 3);
        SaveConfig();
        string patchState = textureFinishPatchApplied ? "已启用" : "补丁未生效，仍为原版 4";
        SetStatus("纹理收尾挡位：" + TextureFinishGearName(textureFinishGear) + " " + TextureFinishLimitForGear(textureFinishGear) + "（" + patchState + "）", true);
    }
    private void InstallAssetCallbackPatch() {
        assetCallbackOwner = this;
        assetCallbackPatchApplied = false;
        assetWorkerTranspilerHits = 0;
        try {
            Type loaderType = typeof(MeshVR.AssetLoader);
            Type requestType = typeof(MeshVR.AssetLoader.AssetBundleFromFileRequest);
            MethodInfo target = AccessTools.Method(loaderType, "CallbackDispatcher");
            MethodInfo prefix = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "AssetCallbackDispatcherPrefix");
            MethodInfo startTarget = AccessTools.Method(loaderType, "Start");
            MethodInfo workerTranspiler = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "AssetWorkerCountTranspiler");
            assetCompletionQueueField = AccessTools.Field(loaderType, "_completionQueue");
            assetRequestLoadCompletedField = AccessTools.Field(requestType, "loadCompleted");
            if (object.ReferenceEquals(target, null) || object.ReferenceEquals(prefix, null)
                || object.ReferenceEquals(startTarget, null) || object.ReferenceEquals(workerTranspiler, null)
                || object.ReferenceEquals(assetCompletionQueueField, null) || object.ReferenceEquals(assetRequestLoadCompletedField, null)) {
                throw new MissingMemberException("reinforced AssetLoader callback members not found");
            }
            assetCallbackHarmony = new Harmony(AssetCallbackHarmonyId);
            assetCallbackHarmony.Patch(target, new HarmonyMethod(prefix));
            assetCallbackHarmony.Patch(startTarget, null, null, new HarmonyMethod(workerTranspiler));
            if (assetWorkerTranspilerHits != 2) throw new InvalidOperationException("expected two AssetLoader worker constants, matched " + assetWorkerTranspilerHits);
            assetCallbackPatchApplied = true;
            DebugLog("Asset callback patch installed. gear=" + AssetCallbackGearName(assetCallbackGear)
                + ", workers=" + AssetWorkerCountForGear(assetCallbackGear)
                + ", loadingBudget=" + AssetCallbackBudgetForGear(assetCallbackGear));
        } catch (Exception e) {
            try { if (assetCallbackHarmony != null) assetCallbackHarmony.UnpatchAll(AssetCallbackHarmonyId); } catch {}
            assetCallbackHarmony = null;
            assetCallbackPatchApplied = false;
            Logger.LogWarning("Asset callback acceleration disabled; ordered dispatcher remains active: " + e.Message);
            DebugLog("Asset callback patch failed: " + e.ToString());
        }
    }
    private void UninstallAssetCallbackPatch() {
        try { if (assetCallbackHarmony != null) assetCallbackHarmony.UnpatchAll(AssetCallbackHarmonyId); }
        catch (Exception e) { Logger.LogWarning("Asset callback patch cleanup failed: " + e.Message); }
        assetCallbackHarmony = null;
        assetCallbackPatchApplied = false;
        if (object.ReferenceEquals(assetCallbackOwner, this)) assetCallbackOwner = null;
    }
    private static bool AssetCallbackDispatcherPrefix(MeshVR.AssetLoader __instance, ref IEnumerator __result) {
        __result = AssetCallbackDispatcherReplacement(__instance);
        return false;
    }
    private static IEnumerable<CodeInstruction> AssetWorkerCountTranspiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
        MethodInfo countMethod = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "AssetWorkerCountForCurrentGear");
        int hits = 0;
        if (!object.ReferenceEquals(countMethod, null)) {
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode != OpCodes.Ldc_I4_8) continue;
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = countMethod;
                hits++;
            }
        }
        assetWorkerTranspilerHits = hits;
        return codes;
    }
    private static int AssetWorkerCountForCurrentGear() {
        AllPackagesLinkerBepInEx owner = assetCallbackOwner;
        return AssetWorkerCountForGear(owner == null ? 0 : owner.assetCallbackGear);
    }
    private static IEnumerator AssetCallbackDispatcherReplacement(MeshVR.AssetLoader loader) {
        while (true) {
            AllPackagesLinkerBepInEx owner = assetCallbackOwner;
            try {
                List<MeshVR.AssetLoader.AssetBundleFromFileRequest> queue = assetCompletionQueueField.GetValue(loader) as List<MeshVR.AssetLoader.AssetBundleFromFileRequest>;
                if (queue != null && queue.Count > 0) {
                    SuperController sc = SuperController.singleton;
                    int gear = owner != null && sc != null && sc.isLoading ? owner.assetCallbackGear : 0;
                    int budget = gear <= 0 ? int.MaxValue : AssetCallbackBudgetForGear(gear);
                    int dispatched = 0;
                    for (int i = 0; i < queue.Count && dispatched < budget;) {
                        MeshVR.AssetLoader.AssetBundleFromFileRequest request = queue[i];
                        bool completed = request != null && (bool)assetRequestLoadCompletedField.GetValue(request);
                        if (!completed) {
                            if (gear <= 0) break;
                            i++;
                            continue;
                        }
                        bool outOfOrder = i > 0;
                        int scanAhead = i;
                        queue.RemoveAt(i);
                        Stopwatch callbackSw = Stopwatch.StartNew();
                        try {
                            if (request.callback != null) request.callback(request);
                        } catch (Exception e) {
                            if (owner != null) owner.DebugLog("Asset callback failed: path=" + request.path + ", error=" + e.ToString());
                        }
                        callbackSw.Stop();
                        if (owner != null) owner.RecordAssetCallback(request.path, outOfOrder, scanAhead, callbackSw.Elapsed.TotalMilliseconds);
                        dispatched++;
                    }
                }
            } catch (Exception e) {
                if (owner != null) owner.DebugLog("Asset callback dispatcher failed: " + e.ToString());
            }
            yield return null;
        }
    }
    private void RecordAssetCallback(string path, bool outOfOrder, int scanAhead, double elapsedMs) {
        if (!sceneLoadProfileActive) return;
        sceneLoadProfileAssetCallbacks++;
        if (outOfOrder) sceneLoadProfileOutOfOrderCallbacks++;
        if (scanAhead > sceneLoadProfileMaxCallbackScanAhead) sceneLoadProfileMaxCallbackScanAhead = scanAhead;
        sceneLoadProfileAssetCallbackWorkMs += elapsedMs;
        if (elapsedMs > sceneLoadProfileSlowestAssetCallbackMs) {
            sceneLoadProfileSlowestAssetCallbackMs = elapsedMs;
            sceneLoadProfileSlowestAssetCallback = SceneLoadProfileLogValue(path);
        }
    }
    private static int AssetCallbackBudgetForGear(int gear) {
        switch (Mathf.Clamp(gear, 0, 3)) {
            case 1: return 2;
            case 2: return 4;
            case 3: return 8;
            default: return 0;
        }
    }
    private static int AssetWorkerCountForGear(int gear) {
        switch (Mathf.Clamp(gear, 0, 3)) {
            case 2: return 12;
            case 3: return 16;
            default: return 8;
        }
    }
    private static string AssetCallbackGearName(int gear) {
        switch (Mathf.Clamp(gear, 0, 3)) {
            case 1: return "均衡";
            case 2: return "高速";
            case 3: return "极限";
            default: return "顺序";
        }
    }
    private void SetAssetCallbackGear(int gear) {
        assetCallbackGear = Mathf.Clamp(gear, 0, 3);
        SaveConfig();
        string patchState = assetCallbackPatchApplied ? "已启用" : "补丁未生效，保持顺序";
        SetStatus("CUA 加载挡位：" + AssetCallbackGearName(assetCallbackGear)
            + " " + AssetWorkerCountForGear(assetCallbackGear) + " Worker / " + AssetCallbackBudgetForGear(assetCallbackGear) + " 回调每帧（" + patchState + "）", true);
    }
    private void InstallLazyCuaPatch() {
        lazyCuaOwner = this;
        lazyCuaPatchApplied = false;
        try {
            MethodInfo syncAssetUrl = AccessTools.Method(typeof(CustomUnityAssetLoader), "SyncAssetUrl");
            MethodInfo syncOn = AccessTools.Method(typeof(Atom), "SyncOn");
            MethodInfo syncAssetUrlPrefix = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "LazyCuaSyncAssetUrlPrefix");
            MethodInfo syncOnPostfix = AccessTools.Method(typeof(AllPackagesLinkerBepInEx), "LazyCuaSyncOnPostfix");
            if (object.ReferenceEquals(syncAssetUrl, null) || object.ReferenceEquals(syncOn, null)
                || object.ReferenceEquals(syncAssetUrlPrefix, null) || object.ReferenceEquals(syncOnPostfix, null)) {
                throw new MissingMemberException("CustomUnityAssetLoader.SyncAssetUrl or Atom.SyncOn members not found");
            }
            lazyCuaSyncAssetUrlMethod = syncAssetUrl;
            lazyCuaHarmony = new Harmony(LazyCuaHarmonyId);
            lazyCuaHarmony.Patch(syncAssetUrl, new HarmonyMethod(syncAssetUrlPrefix));
            lazyCuaHarmony.Patch(syncOn, null, new HarmonyMethod(syncOnPostfix));
            lazyCuaPatchApplied = true;
            DebugLog("Lazy disabled CUA patch installed. enabled=" + lazyDisabledCuaEnabled);
        } catch (Exception e) {
            try { if (lazyCuaHarmony != null) lazyCuaHarmony.UnpatchAll(LazyCuaHarmonyId); } catch {}
            lazyCuaHarmony = null;
            lazyCuaPatchApplied = false;
            lazyCuaSyncAssetUrlMethod = null;
            Logger.LogWarning("Lazy disabled CUA loading disabled; original CUA behavior remains active: " + e.Message);
            DebugLog("Lazy disabled CUA patch failed: " + e.ToString());
        }
    }
    private void UninstallLazyCuaPatch() {
        try { if (lazyCuaHarmony != null) lazyCuaHarmony.UnpatchAll(LazyCuaHarmonyId); }
        catch (Exception e) { Logger.LogWarning("Lazy disabled CUA patch cleanup failed: " + e.Message); }
        lazyCuaHarmony = null;
        lazyCuaPatchApplied = false;
        lazyCuaSyncAssetUrlMethod = null;
        if (object.ReferenceEquals(lazyCuaOwner, this)) lazyCuaOwner = null;
    }
    private static bool LazyCuaSyncAssetUrlPrefix(CustomUnityAssetLoader __instance, string __0) {
        AllPackagesLinkerBepInEx owner = lazyCuaOwner;
        if (!lazyCuaPatchApplied || owner == null || !owner.lazyDisabledCuaEnabled || string.IsNullOrEmpty(__0)) return true;
        if (owner.activatingDeferredCuaLoads.Contains(__instance)) return true;
        SuperController sc = SuperController.singleton;
        Atom atom = __instance == null ? null : __instance.containingAtom;
        if (!owner.sceneLoadProfileActive || sc == null || !sc.isLoading || atom == null
            || !owner.lazyCuaCandidateAtomUids.Contains(atom.uid)) return true;
        owner.DeferCuaLoad(__instance, __0);
        return false;
    }
    private static void LazyCuaSyncOnPostfix(Atom __instance, bool __0) {
        AllPackagesLinkerBepInEx owner = lazyCuaOwner;
        if (__0 && lazyCuaPatchApplied && owner != null && owner.lazyDisabledCuaEnabled) owner.ActivateDeferredCuaLoads(__instance, "atom-enabled");
    }
    private void DeferCuaLoad(CustomUnityAssetLoader loader, string path) {
        if (loader == null || string.IsNullOrEmpty(path)) return;
        bool added = !deferredCuaUrls.ContainsKey(loader);
        deferredCuaUrls[loader] = path;
        if (!added) return;
        sceneLoadProfileDeferredCua++;
        int count = deferredCuaUrls.Count;
        if (count == 1 || count % 10 == 0) DebugLog("Lazy disabled CUA queued. pending=" + count);
    }
    private void PrepareLazyCuaCandidates(string sceneJson) {
        lazyCuaCandidateAtomUids.Clear();
        if (!lazyDisabledCuaEnabled || string.IsNullOrEmpty(sceneJson)) return;
        try {
            SceneJsonAnalysis analysis = new SceneJsonAnalysis();
            string error;
            if (!TryAnalyzeSceneAtoms(sceneJson, analysis, out error)) throw new InvalidDataException(error);
            for (int i = 0; i < analysis.atoms.Count; i++) {
                SceneAtomSpan atom = analysis.atoms[i];
                if (!string.Equals(atom.type, "CustomUnityAsset", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(atom.id)) continue;
                string on;
                if (TryFindDirectStringProperty(sceneJson, atom.start, atom.start + atom.length, "on", out on)
                    && string.Equals(on, "false", StringComparison.OrdinalIgnoreCase)) lazyCuaCandidateAtomUids.Add(atom.id);
            }
            DebugLog("Lazy disabled CUA candidates prepared. count=" + lazyCuaCandidateAtomUids.Count);
        } catch (Exception e) {
            lazyCuaCandidateAtomUids.Clear();
            DebugLog("Lazy disabled CUA candidate scan failed; original loading remains active: " + e.Message);
        }
    }
    private void PollDeferredCuaLoads() {
        if (deferredCuaUrls.Count == 0 || Time.realtimeSinceStartup < nextDeferredCuaPollAt) return;
        nextDeferredCuaPollAt = Time.realtimeSinceStartup + 0.25f;
        List<CustomUnityAssetLoader> loaders = new List<CustomUnityAssetLoader>(deferredCuaUrls.Keys);
        for (int i = 0; i < loaders.Count; i++) {
            CustomUnityAssetLoader loader = loaders[i];
            if (loader == null || loader.containingAtom == null) {
                deferredCuaUrls.Remove(loader);
                continue;
            }
            if (loader.containingAtom.on) ActivateDeferredCuaLoad(loader, "poll-enabled");
        }
    }
    private void ActivateDeferredCuaLoads(Atom atom, string reason) {
        if (atom == null || deferredCuaUrls.Count == 0) return;
        List<CustomUnityAssetLoader> loaders = new List<CustomUnityAssetLoader>(deferredCuaUrls.Keys);
        for (int i = 0; i < loaders.Count; i++) {
            CustomUnityAssetLoader loader = loaders[i];
            if (loader != null && loader.containingAtom == atom) ActivateDeferredCuaLoad(loader, reason);
        }
    }
    private void ActivateDeferredCuaLoad(CustomUnityAssetLoader loader, string reason) {
        string path;
        if (loader == null || !deferredCuaUrls.TryGetValue(loader, out path)) return;
        deferredCuaUrls.Remove(loader);
        activatingDeferredCuaLoads.Add(loader);
        try {
            if (object.ReferenceEquals(lazyCuaSyncAssetUrlMethod, null)) throw new MissingMethodException("CustomUnityAssetLoader.SyncAssetUrl");
            lazyCuaSyncAssetUrlMethod.Invoke(loader, new object[] { path });
            sceneLoadProfileActivatedDeferredCua++;
            DebugLog("Lazy CUA activated. atom=" + SceneLoadProfileLogValue(loader.containingAtom == null ? "-" : loader.containingAtom.uid)
                + ", reason=" + reason + ", remaining=" + deferredCuaUrls.Count);
        } catch (Exception e) {
            if (loader != null) deferredCuaUrls[loader] = path;
            DebugLog("Lazy CUA activation failed. reason=" + reason + ", path=" + SceneLoadProfileLogValue(path) + ", error=" + e.ToString());
        } finally {
            activatingDeferredCuaLoads.Remove(loader);
        }
    }
    private void FlushDeferredCuaLoads(string reason) {
        if (deferredCuaUrls.Count == 0) return;
        List<CustomUnityAssetLoader> loaders = new List<CustomUnityAssetLoader>(deferredCuaUrls.Keys);
        for (int i = 0; i < loaders.Count; i++) ActivateDeferredCuaLoad(loaders[i], reason);
    }
    private void SetLazyDisabledCuaEnabled(bool enabled) {
        lazyDisabledCuaEnabled = enabled;
        SaveConfig();
        if (!enabled) FlushDeferredCuaLoads("settings-off");
        string patchState = lazyCuaPatchApplied ? "已启用" : "补丁未生效，保持原版";
        SetStatus("关闭 CUA 延迟加载：" + (enabled ? "开" : "关") + "（" + patchState + "）", true);
    }
    private void StopActiveHubProcess() {
        Process p = activeHubProcess; activeHubProcess = null;
        try { if (p != null && !p.HasExited) p.Kill(); } catch {}
        try { if (p != null) p.Dispose(); } catch {}
    }

    private void SetStatus(string s, bool log) {
        status = s;
        if (header != null) header.text = "AllPackagesLinker";
        if (statusText != null) statusText.text = OneLine(s, 160);
        if (log) {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("s") + " " + s + Environment.NewLine, Encoding.UTF8); } catch {}
            DebugLog("STATUS: " + s);
            Logger.LogInfo(s);
        }
    }

    private void DebugLog(string s) {
        try {
            string line = DateTime.Now.ToString("s") + " [AllPackagesLinker] " + s + Environment.NewLine;
            if (string.IsNullOrEmpty(debugLogPath)) {
                string root = "";
                try { root = Directory.GetParent(Application.dataPath).FullName; } catch {}
                if (root != "") {
                    string dir = Path.Combine(root, "Saves\\PluginData\\AllPackagesLinker");
                    Directory.CreateDirectory(dir);
                    debugLogPath = Path.Combine(dir, "debug.log");
                }
            }
            if (!string.IsNullOrEmpty(debugLogPath)) File.AppendAllText(debugLogPath, line, Encoding.UTF8);
        } catch {}
        try { Logger.LogInfo("[DEBUG] " + s); } catch {}
    }

    private string HeaderText() { return "AllPackagesLinker"; }
    private string CatLabel(string cat) {
        if(cat=="Favorites") return "收藏";
        if(cat=="Scenes") return "场景";
        if(cat=="Looks") return "预设";
        if(cat=="Clothing") return "服装";
        if(cat=="Hair") return "头发";
        if(cat=="Morphs") return "形态";
        if(cat=="Presets") return "预设";
        if(cat=="Plugins") return "插件";
        if(cat=="Assets") return "资产";
        if(cat=="Scripts") return "脚本";
        if(cat=="Other") return "其他";
        if(cat=="All") return "全部";
        return cat;
    }
    private string OneLine(string s, int n) { if (s == null) return ""; s=s.Replace('\r',' ').Replace('\n',' '); return s.Length<=n?s:s.Substring(0,n)+"..."; }

    private void TogglePanel() {
        DebugLog("TogglePanel begin. canvasExists=" + (canvas != null) + ", scanned=" + scanned + ", scanning=" + scanning);
        try { if (canvas != null) ClosePanel(); else OpenPanel(); }
        catch(Exception e) { DebugLog("TogglePanel FAILED: " + e.ToString()); Logger.LogError(e); SetStatus("菜单呼出失败：" + e.Message, true); }
        DebugLog("TogglePanel end. canvasExists=" + (canvas != null));
    }
    private void TryAutoOpenPanelOnPluginLoad() {
        try {
            if (!autoOpenPanelOnPluginLoad) {
                DebugLog("TryAutoOpenPanelOnPluginLoad skipped: config off.");
                return;
            }
            if (canvas != null) {
                DebugLog("TryAutoOpenPanelOnPluginLoad skipped: panel already open.");
                return;
            }
            if (SuperController.singleton == null) {
                if (autoOpenRetryCount < MaxAutoOpenRetries) {
                    autoOpenRetryCount++;
                    DebugLog("TryAutoOpenPanelOnPluginLoad wait SuperController. retry=" + autoOpenRetryCount + "/" + MaxAutoOpenRetries);
                    Invoke("TryAutoOpenPanelOnPluginLoad", 1.0f);
                } else {
                    DebugLog("TryAutoOpenPanelOnPluginLoad give up: SuperController never ready.");
                }
                return;
            }
            // In VR, open as world-space panel so the headset can see it.
            // Desktop overlay is invisible inside the HMD (previous bug).
            bool vrActive = IsVrSessionActive();
            openedViaVR = vrActive;
            if (vrActive && !CanPlaceVrCanvas()) {
                if (autoOpenRetryCount < MaxAutoOpenRetries) {
                    autoOpenRetryCount++;
                    DebugLog("TryAutoOpenPanelOnPluginLoad wait VR view. retry=" + autoOpenRetryCount + "/" + MaxAutoOpenRetries);
                    Invoke("TryAutoOpenPanelOnPluginLoad", 0.75f);
                    return;
                }
                DebugLog("TryAutoOpenPanelOnPluginLoad VR view timeout; desktop overlay fallback.");
                openedViaVR = false;
            }
            DebugLog("Auto opening panel after plugin load. vr=" + openedViaVR + ", isVrSession=" + vrActive + ", canPlace=" + CanPlaceVrCanvas());
            OpenPanel();
            if (canvas == null && autoOpenRetryCount < MaxAutoOpenRetries) {
                autoOpenRetryCount++;
                DebugLog("TryAutoOpenPanelOnPluginLoad OpenPanel left canvas null; retry=" + autoOpenRetryCount);
                Invoke("TryAutoOpenPanelOnPluginLoad", 1.0f);
            }
        } catch(Exception e) {
            DebugLog("TryAutoOpenPanelOnPluginLoad FAILED: " + e.ToString());
            if (autoOpenRetryCount < MaxAutoOpenRetries) {
                autoOpenRetryCount++;
                try { Invoke("TryAutoOpenPanelOnPluginLoad", 1.0f); } catch {}
            }
        }
    }
    private void TryAutoOpenPanelInEditMode() {
        try {
            if (autoOpenPanelOnPluginLoad) return; // already covered by plugin-load open
            if (!autoOpenPanelInEditMode) return;
            if (canvas != null) return;
            if (isVRMode) return;
            if (SuperController.singleton == null) return;
            if ((int)SuperController.singleton.gameMode != 0) return;
            DebugLog("Auto opening panel in Edit mode.");
            openedViaVR = false;
            OpenPanel();
        } catch(Exception e) {
            DebugLog("TryAutoOpenPanelInEditMode FAILED: " + e.ToString());
        }
    }
    private void AutoAllowAllPendingPluginPackages() {
        AutoAllowPendingPluginPackages(autoAllowAllPlugins);
    }
    private void ForceAllowPendingPluginPackages() {
        AutoAllowPendingPluginPackages(true);
    }
    private void AutoAllowPendingPluginPackages(bool force) {
        try {
            if (!force) return;
            UnityEngine.Object[] objs = Resources.FindObjectsOfTypeAll(typeof(MonoBehaviour));
            int approved = 0;
            for (int i = 0; i < objs.Length; i++) {
                MonoBehaviour plugin = objs[i] as MonoBehaviour;
                if (plugin == null) continue;
                Type pt = plugin.GetType();
                if (object.ReferenceEquals(pt, null) || pt.Name != "MVRPlugin") continue;
                System.Reflection.FieldInfo reqField = pt.GetField("requestedPackages", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (object.ReferenceEquals(reqField, null)) continue;
                object reqObj = reqField.GetValue(plugin);
                System.Collections.IEnumerable reqs = reqObj as System.Collections.IEnumerable;
                if (reqs == null) continue;
                foreach (VarPackage pkg in reqs) {
                    if (pkg == null) continue;
                    try {
                        System.Reflection.MethodInfo isConfirmedMethod = pt.GetMethod("IsVarPackageConfirmed");
                        System.Reflection.MethodInfo confirmMethod = pt.GetMethod("UserConfirmStickyVarPackage");
                        if (object.ReferenceEquals(isConfirmedMethod, null) || object.ReferenceEquals(confirmMethod, null)) continue;
                        bool isConfirmed = (bool)isConfirmedMethod.Invoke(plugin, new object[] { pkg });
                        if (!isConfirmed) {
                            confirmMethod.Invoke(plugin, new object[] { pkg });
                            approved++;
                        }
                    } catch {}
                }
            }
            if (approved > 0) DebugLog("AutoAllowAllPendingPluginPackages approved=" + approved);
        } catch(Exception e) {
            DebugLog("AutoAllowAllPendingPluginPackages FAILED: " + e.ToString());
        }
    }
    private string GetSelectedPackageUid() {
        if (selectedVarPreset != null && selectedVarPreset.package != null) return selectedVarPreset.package.uid;
        if (selectedSceneItem != null && selectedSceneItem.package != null) return selectedSceneItem.package.uid;
        if (selectedWearableItem != null && selectedWearableItem.package != null) return selectedWearableItem.package.uid;
        if (selected != null) return selected.uid;
        return "";
    }
    private PackageLite GetSelectedPackage() {
        if (selectedVarPreset != null) return selectedVarPreset.package;
        if (selectedSceneItem != null) return selectedSceneItem.package;
        if (selectedWearableItem != null) return selectedWearableItem.package;
        return selected;
    }
    private void OpenSelectedInHub() {
        try {
            string uid = GetSelectedPackageUid();
            if (string.IsNullOrEmpty(uid)) { SetStatus("当前没有可打开 Hub 的包。", true); return; }
            if (SuperController.singleton == null || SuperController.singleton.hubBrowser == null) { SetStatus("Hub Browser 不可用。", true); return; }
            SuperController.singleton.OpenHub();
            string resourceId = SuperController.singleton.hubBrowser.GetPackageHubResourceId(uid);
            if (!string.IsNullOrEmpty(resourceId)) {
                SuperController.singleton.hubBrowser.OpenDetail(resourceId, true);
                SetStatus("已打开 Hub详情：" + uid, true);
            } else {
                if (SuperController.singleton.packageDownloader != null) {
                    SuperController.singleton.packageDownloader.FindPackage(uid, true);
                    SetStatus("未找到直达详情，已转到 Hub搜索：" + uid, true);
                } else {
                    SetStatus("未找到 Hub 资源映射：" + uid, true);
                }
            }
        } catch (Exception e) {
            DebugLog("OpenSelectedInHub FAILED: " + e.ToString());
            SetStatus("打开 Hub失败：" + e.Message, true);
        }
    }
    private void SearchSelectedInHub() {
        try {
            string uid = GetSelectedPackageUid();
            if (string.IsNullOrEmpty(uid)) { SetStatus("当前没有可搜索的包。", true); return; }
            if (SuperController.singleton == null || SuperController.singleton.packageDownloader == null) { SetStatus("Hub Downloader 不可用。", true); return; }
            SuperController.singleton.OpenHub();
            SuperController.singleton.packageDownloader.FindPackage(uid, true);
            SetStatus("已在 Hub中搜索：" + uid, true);
        } catch (Exception e) {
            DebugLog("SearchSelectedInHub FAILED: " + e.ToString());
            SetStatus("Hub搜索失败：" + e.Message, true);
        }
    }
    private void CheckSelectedMissingDeps() {
        try {
            DebugLog("CheckSelectedMissingDeps begin. " + DescribeSelectionState());
            PackageLite p = GetSelectedPackage();
            if (p == null) { SetStatus("当前没有可检查依赖的包。", true); return; }
            lastDepsCheckedPackage = p;
            DepCheckResult depCheck = AnalyzeDirectDeps(p);
            StringBuilder sb = new StringBuilder();
            sb.Append(p.uid).Append("\n\n依赖统计：Addon=").Append(depCheck.haveAddon.Count).Append("，库中可补链=").Append(depCheck.haveLibrary.Count).Append("，缺失=").Append(depCheck.missing.Count);
            if (depCheck.missing.Count > 0) {
                sb.Append("\n\n缺失依赖：\n");
                for (int i = 0; i < depCheck.missing.Count && i < 24; i++) sb.Append("- ").Append(depCheck.missing[i]).Append("\n");
            }
            if (depCheck.haveLibrary.Count > 0) {
                sb.Append("\n说明：\"库中可补链\" 表示这些依赖已经在你的本地包库里，只是还没进入 AddonPackages。");
                if (depCheck.missing.Count == 0) sb.Append("这种情况不需要再走 Hub 下载，直接点“仅链接”或“加载场景”即可。");
            }
            if (details != null) details.text = sb.ToString();
            SetStatus("依赖检查完成：缺失=" + depCheck.missing.Count + "，库中可补链=" + depCheck.haveLibrary.Count, true);
        } catch (Exception e) {
            DebugLog("CheckSelectedMissingDeps FAILED: " + e.ToString());
            SetStatus("检查依赖失败：" + e.Message, true);
        }
    }

    private void DownloadSelectedMissingDepsToLibrary() {
        try {
            DebugLog("DownloadSelectedMissingDepsToLibrary begin. " + DescribeSelectionState());
            if (missingDepsDownloadRunning) {
                missingDepsDownloadCancelRequested = true;
                SetStatus("正在取消 Hub 下载...", true);
                if (hubDownloadButton != null) { hubDownloadButton.GetComponentInChildren<Text>().text = "正在取消..."; hubDownloadButton.interactable = false; }
                return;
            }
            PackageLite p = GetSelectedPackage();
            if (p == null && lastDepsCheckedPackage != null && CanOpenVarFile(lastDepsCheckedPackage.fullPath)) {
                p = lastDepsCheckedPackage;
                DebugLog("DownloadSelectedMissingDepsToLibrary fallback to lastDepsCheckedPackage=" + p.uid);
            }
            if (p == null) { SetStatus("当前没有可下载依赖的包。请先在列表里选中一个包、包内预设或场景，或先点“查缺失依赖”。", true); return; }
            lastDepsCheckedPackage = p;
            DepCheckResult depCheck = AnalyzeDirectDeps(p);
            List<string> missing = GetMissingDepsForPackage(p);
            if (missing.Count == 0) {
                if (depCheck.haveLibrary.Count > 0) {
                    LinkResult linkResult = LinkWithDeps(p);
                    RefreshVam();
                    if (canvas != null) RefreshList();
                    string msg = "没有需要从 Hub 下载的依赖；已执行本地补链：库中可补链=" + depCheck.haveLibrary.Count
                        + "，新建=" + linkResult.created + "，已存在=" + linkResult.already + "，仍缺失=" + linkResult.missing.Count + "，错误=" + linkResult.errors.Count;
                    if (linkResult.missing.Count > 0) msg += " | 仍缺：" + string.Join(", ", linkResult.missing.ToArray());
                    if (linkResult.errors.Count > 0) msg += " | 错误：" + string.Join("；", linkResult.errors.ToArray());
                    SetStatus(msg, true);
                } else {
                    SetStatus("没有缺失依赖需要下载：" + p.uid, true);
                }
                return;
            }
            missingDepsDownloadCancelRequested = false;
            SetDownloadProgress(0f, "准备查询 Hub...");
            if (hubDownloadButton != null) hubDownloadButton.GetComponentInChildren<Text>().text = "取消下载";
            StartCoroutine(DownloadMissingDepsCoroutine(p, missing));
        } catch(Exception e) {
            DebugLog("DownloadSelectedMissingDepsToLibrary FAILED: " + e.ToString());
            SetStatus("启动依赖下载失败：" + e.Message, true);
        }
    }

    private DepCheckResult AnalyzeDirectDeps(PackageLite p) {
        DepCheckResult result = new DepCheckResult();
        if (p == null) return result;
        for (int i = 0; i < p.deps.Count; i++) {
            string d = (p.deps[i] ?? "").Trim();
            if (d == "") continue;
            if (IsAvailableInAddon(d)) { result.haveAddon.Add(d); continue; }
            PackageLite dp; bool already;
            if (ResolveDep(d, out dp, out already) && dp != null) {
                if (already) result.haveAddon.Add(d);
                else result.haveLibrary.Add(d);
            } else result.missing.Add(d);
        }
        return result;
    }

    private List<string> GetMissingDepsForPackage(PackageLite p) {
        List<string> missing = new List<string>();
        HashSet<string> seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> seenMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectMissingDepsRecursive(p, seenPackages, seenMissing, missing);
        missing.Sort(StringComparer.OrdinalIgnoreCase);
        return missing;
    }

    private void CollectMissingDepsRecursive(PackageLite p, HashSet<string> seenPackages, HashSet<string> seenMissing, List<string> missing) {
        if (p == null || !seenPackages.Add(p.uid)) return;
        for (int i = 0; i < p.deps.Count; i++) {
            string d = (p.deps[i] ?? "").Trim();
            if (d == "") continue;
            PackageLite dp; bool already;
            if (ResolveDep(d, out dp, out already) && dp != null) {
                if (!already) CollectMissingDepsRecursive(dp, seenPackages, seenMissing, missing);
            } else if (seenMissing.Add(d)) {
                missing.Add(d);
            }
        }
    }

    private IEnumerator DownloadMissingDepsCoroutine(PackageLite sourcePackage, List<string> missing) {
        missingDepsDownloadRunning = true;
        int downloaded = 0, skipped = 0, notOnHub = 0, failed = 0;
        List<string> errors = new List<string>();
        List<string> availableFileNames = new List<string>();
        try {
            Directory.CreateDirectory(missingDepsDownloadRoot);
            SetDownloadProgress(0f, "正在连接 VaM Hub...");
            SetStatus("正在查询 Hub 下载信息：缺失依赖 " + missing.Count + " 个；目标库=" + missingDepsDownloadRoot, true);
            Dictionary<string, HubDownloadInfo> infos = null;
            string queryErr = "";
            yield return StartCoroutine(QueryHubDownloadInfos(missing, (Dictionary<string, HubDownloadInfo> r, string err) => { infos = r; queryErr = err; }));
            if (missingDepsDownloadCancelRequested) { SetDownloadProgress(0f, "下载已取消"); SetStatus("Hub 下载已取消。", true); yield break; }
            if (!string.IsNullOrEmpty(queryErr)) {
                SetDownloadProgress(0f, "Hub连接失败：" + OneLine(queryErr, 100));
                SetStatus("Hub 查询失败：" + queryErr, true);
                yield break;
            }
            if (infos == null) infos = new Dictionary<string, HubDownloadInfo>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < missing.Count; i++) {
                if (missingDepsDownloadCancelRequested) break;
                string dep = missing[i];
                HubDownloadInfo info;
                if (!infos.TryGetValue(dep, out info) || info == null || string.IsNullOrEmpty(info.downloadUrl) || info.downloadUrl == "null" || string.IsNullOrEmpty(info.filename) || info.filename == "null") {
                    notOnHub++;
                    errors.Add(dep + ": Hub无可下载地址");
                    SetDownloadProgress((i + 1f) / Math.Max(1f, missing.Count), dep + "：Hub无可下载地址");
                    continue;
                }
                string safeName = SafeFileName(info.filename);
                if (safeName == "" || !safeName.EndsWith(".var", StringComparison.OrdinalIgnoreCase)) {
                    notOnHub++;
                    errors.Add(dep + ": Hub返回文件名异常(" + info.filename + ")");
                    SetDownloadProgress((i + 1f) / Math.Max(1f, missing.Count), dep + "：Hub文件名异常");
                    continue;
                }
                if (!HubPackageSatisfiesRequest(dep, safeName)) {
                    notOnHub++;
                    errors.Add(dep + ": Hub仅返回不兼容版本 " + Path.GetFileNameWithoutExtension(safeName));
                    SetDownloadProgress((i + 1f) / Math.Max(1f, missing.Count), dep + "：Hub没有所需版本");
                    continue;
                }
                if (!IsSafeDownloadUrl(info.downloadUrl)) {
                    failed++;
                    errors.Add(dep + ": Hub返回了不安全的下载地址");
                    SetDownloadProgress((i + 1f) / Math.Max(1f, missing.Count), dep + "：下载地址被拒绝");
                    continue;
                }
                string finalPath = Path.Combine(missingDepsDownloadRoot, safeName);
                long expectedSize = ParseHubFileSize(info.fileSize);
                if (File.Exists(finalPath) && IsValidDownloadedVar(finalPath, expectedSize)) {
                    skipped++;
                    availableFileNames.Add(safeName);
                    DebugLog("Dependency already exists in download root: " + finalPath);
                    SetDownloadProgress((i + 1f) / Math.Max(1f, missing.Count), safeName + "：本地已存在");
                    continue;
                }
                try { if (File.Exists(finalPath)) { DebugLog("Deleting invalid existing Hub file before retry: " + finalPath); File.Delete(finalPath); } } catch(Exception e) { failed++; errors.Add(dep + ": 无法替换损坏文件 " + e.Message); continue; }
                string tmpPath = finalPath + ".download";
                string dlErr = "";
                SetStatus("下载缺失依赖 " + (i + 1) + "/" + missing.Count + "：" + safeName, true);
                yield return StartCoroutine(DownloadOneVar(info.downloadUrl, tmpPath, finalPath, expectedSize, (float progress, long received, long total, double bytesPerSecond) => {
                    string totalText = total > 0 ? FormatBytes(total) : "未知大小";
                    string eta = (total > received && bytesPerSecond > 1.0) ? ("，剩余" + FormatDuration((total - received) / bytesPerSecond)) : "";
                    string percentText = total > 0 ? (Mathf.RoundToInt(progress * 100f) + "%  ") : "";
                    SetDownloadProgress((i + progress) / Math.Max(1f, missing.Count), "下载 " + (i + 1) + "/" + missing.Count + "：" + safeName + "  " + percentText + FormatBytes(received) + "/" + totalText + "  " + (bytesPerSecond > 1 ? FormatBytes((long)bytesPerSecond) + "/s" : "等待网络") + eta);
                }, (string err) => { dlErr = err; }));
                if (missingDepsDownloadCancelRequested || dlErr == "用户取消") break;
                if (string.IsNullOrEmpty(dlErr)) {
                    downloaded++;
                    availableFileNames.Add(safeName);
                    DebugLog("Downloaded missing dependency: " + dep + " -> " + finalPath);
                } else {
                    failed++;
                    errors.Add(dep + ": " + dlErr);
                    DebugLog("Download missing dependency failed: " + dep + " | " + dlErr);
                }
            }

            if (missingDepsDownloadCancelRequested) {
                SetDownloadProgress(downloadProgressValue, "下载已取消；已完成 " + downloaded + " 个");
                SetStatus("Hub 下载已取消：已完成=" + downloaded + "，已存在=" + skipped + "。已下载完成的文件会保留。", true);
                yield break;
            }
            SetDownloadProgress(1f, "下载完成，正在刷新 VaM 包索引...");
            SetStatus("下载完成，正在刷新 VaM 包索引...", true);
            int indexedDownloads = IndexDownloadedPackages(availableFileNames);
            LinkResult linkResult = LinkWithDeps(sourcePackage);
            RefreshVam();
            if (canvas != null) RefreshList();

            string msg = "缺失依赖下载完成：下载=" + downloaded + "，已存在=" + skipped + "，Hub无地址=" + notOnHub + "，失败=" + failed
                + " | 快速入库=" + indexedDownloads + " | 自动补链：新建=" + linkResult.created + "，已存在=" + linkResult.already + "，仍缺失=" + linkResult.missing.Count;
            if (linkResult.missing.Count > 0) msg += " | 仍缺：" + string.Join(", ", linkResult.missing.ToArray());
            if (errors.Count > 0) msg += " | 详情：" + string.Join("；", errors.GetRange(0, Math.Min(errors.Count, 8)).ToArray());
            SetDownloadProgress(1f, "完成：下载=" + downloaded + "，已存在=" + skipped + "，无地址/版本不符=" + notOnHub + "，失败=" + failed);
            SetStatus(msg, true);
            if (sourcePackage != null && details != null) SelectPackage(sourcePackage);
        } finally {
            missingDepsDownloadRunning = false;
            missingDepsDownloadCancelRequested = false;
            if (hubDownloadButton != null) { hubDownloadButton.GetComponentInChildren<Text>().text = "Hub下载/本地补链"; hubDownloadButton.interactable = true; }
        }
    }

    private delegate void HubInfoCallback(Dictionary<string, HubDownloadInfo> infos, string err);
    private IEnumerator QueryHubDownloadInfos(List<string> packages, HubInfoCallback callback) {
        Dictionary<string, HubDownloadInfo> result = new Dictionary<string, HubDownloadInfo>(StringComparer.OrdinalIgnoreCase);
        string err = "";
        string jsonPath = "";
        try {
            JSONClass jc = new JSONClass();
            jc["source"] = "VaM";
            jc["action"] = "findPackages";
            jc["packages"] = string.Join(",", packages.ToArray());
            Directory.CreateDirectory(dataRoot);
            jsonPath = Path.Combine(dataRoot, "hub_findpackages_request.json");
            // Hub rejects JSON files with UTF-8 BOM when sent through curl --data-binary.
            File.WriteAllText(jsonPath, jc.ToString(), new UTF8Encoding(false));
        } catch(Exception e) {
            if (callback != null) callback(result, e.Message);
            yield break;
        }

        string stdout = "", stderr = "";
        int exitCode = -1;
        string args = "-sS -L --retry 3 --retry-all-errors --retry-delay 2 --connect-timeout 15 -X POST " + ArgQ(HubApiUrl) + " -H " + ArgQ("Content-Type: application/json") + " --data-binary " + ArgQ("@" + jsonPath) + " --max-time 75";
        yield return StartCoroutine(RunProcessCoroutine(ResolveCurlExecutable(), args, 85, (int code, string so, string se) => { exitCode = code; stdout = so; stderr = se; }));
        try {
            if (exitCode != 0) {
                err = "curl 查询失败(exit=" + exitCode + ") " + OneLine(stderr, 220);
            } else {
                err = ParseHubDownloadInfos(stdout, packages, result);
            }
        } catch(Exception e) {
            err = e.Message;
        } finally {
            try { if (File.Exists(jsonPath)) File.Delete(jsonPath); } catch {}
        }
        if (callback != null) callback(result, err);
    }

    private delegate void DownloadCallback(string err);
    private delegate void DownloadProgressCallback(float progress, long received, long total, double bytesPerSecond);
    private IEnumerator DownloadOneVar(string url, string tmpPath, string finalPath, long expectedTotal, DownloadProgressCallback progressCallback, DownloadCallback callback) {
        string err = "", stderrPath = tmpPath + ".curl-error.txt";
        Process pr = null; bool started = false; int exitCode = -1;
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(finalPath));
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
            if (File.Exists(stderrPath)) File.Delete(stderrPath);
            string args = "-fL --retry 5 --retry-all-errors --retry-delay 2 --connect-timeout 20 --speed-limit 1024 --speed-time 45 --max-time 1800 --silent --show-error --stderr "
                + ArgQ(stderrPath) + " -o " + ArgQ(tmpPath) + " " + ArgQ(url);
            ProcessStartInfo psi = new ProcessStartInfo(ResolveCurlExecutable(), args);
            psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden; psi.UseShellExecute = false;
            pr = Process.Start(psi); started = pr != null; if (started) activeHubProcess = pr;
        } catch(Exception e) { err = "无法启动 curl：" + e.Message; }

        if (started) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(1810);
            long lastBytes = 0; float lastTime = Time.realtimeSinceStartup; double speed = 0;
            while (true) {
                bool exited = true;
                try { exited = pr == null || pr.HasExited; } catch(Exception e) { err = e.Message; }
                if (exited || !string.IsNullOrEmpty(err)) break;
                if (missingDepsDownloadCancelRequested) { try { pr.Kill(); } catch {} err = "用户取消"; break; }
                if (DateTime.UtcNow > deadline) { try { pr.Kill(); } catch {} err = "下载超时"; break; }
                long received = 0; try { if (File.Exists(tmpPath)) received = new FileInfo(tmpPath).Length; } catch {}
                float now = Time.realtimeSinceStartup;
                if (now - lastTime >= 0.25f) { speed = (received - lastBytes) / Math.Max(0.01, now - lastTime); lastBytes = received; lastTime = now; }
                if (progressCallback != null) progressCallback(expectedTotal > 0 ? Mathf.Clamp01((float)received / expectedTotal) : 0f, received, expectedTotal, speed);
                yield return null;
            }
            try { if (pr != null && pr.HasExited) exitCode = pr.ExitCode; } catch {}
        }
        if (object.ReferenceEquals(activeHubProcess, pr)) activeHubProcess = null;
        try { if (pr != null) pr.Dispose(); } catch {}
        if (string.IsNullOrEmpty(err) && (!started || exitCode != 0)) {
            string se = ""; try { if (File.Exists(stderrPath)) se = File.ReadAllText(stderrPath); } catch {}
            err = "curl 下载失败(exit=" + exitCode + ") " + OneLine(se, 260);
        }
        if (string.IsNullOrEmpty(err)) {
            try {
                if (!File.Exists(tmpPath) || new FileInfo(tmpPath).Length == 0) throw new Exception("下载文件为空");
                long actualLength = new FileInfo(tmpPath).Length;
                if (expectedTotal > 0 && actualLength != expectedTotal) throw new Exception("文件大小校验失败：收到 " + FormatBytes(actualLength) + "，预期 " + FormatBytes(expectedTotal));
                using (FileStream verify = File.Open(tmpPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    int b0 = verify.ReadByte(), b1 = verify.ReadByte();
                    if (b0 != 0x50 || b1 != 0x4B) throw new Exception("下载内容不是有效的 VAR/ZIP 文件");
                }
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(tmpPath, finalPath);
            } catch(Exception e) { err = e.Message; }
        }
        try { if (File.Exists(stderrPath)) File.Delete(stderrPath); } catch {}
        if (!string.IsNullOrEmpty(err)) { try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch {} }
        if (callback != null) callback(err);
    }

    private string ResolveCurlExecutable() {
        try { string systemCurl = Path.Combine(Environment.SystemDirectory, "curl.exe"); if (File.Exists(systemCurl)) return systemCurl; } catch {}
        return "curl.exe";
    }
    private bool HubPackageSatisfiesRequest(string request, string filename) {
        string actual = Path.GetFileNameWithoutExtension(filename ?? "");
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(request)) return false;
        if (request.EndsWith(".latest", StringComparison.OrdinalIgnoreCase))
            return string.Equals(Group(actual) + ".latest", request, StringComparison.OrdinalIgnoreCase);
        return string.Equals(actual, request, StringComparison.OrdinalIgnoreCase);
    }
    private bool IsSafeDownloadUrl(string value) {
        try { Uri u; return Uri.TryCreate(value, UriKind.Absolute, out u) && u.Scheme == Uri.UriSchemeHttps; }
        catch { return false; }
    }
    private bool IsValidDownloadedVar(string path, long expectedSize) {
        try {
            FileInfo fi = new FileInfo(path);
            if (!fi.Exists || fi.Length < 4 || (expectedSize > 0 && fi.Length != expectedSize)) return false;
            using (FileStream fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) return fs.ReadByte() == 0x50 && fs.ReadByte() == 0x4B;
        } catch { return false; }
    }
    private int IndexDownloadedPackages(List<string> fileNames) {
        if (fileNames == null || fileNames.Count == 0) return 0;
        HashSet<string> wanted = new HashSet<string>(fileNames, StringComparer.OrdinalIgnoreCase);
        HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int indexed = 0;
        try {
            foreach (string path in GetVarFiles(allRoot, true)) {
                string name = Path.GetFileName(path);
                if (!wanted.Contains(name) || !found.Add(name)) continue;
                try { UpsertDownloadedPackage(ReadPackage(path, allRoot, true)); indexed++; }
                catch(Exception e) { DebugLog("Fast index downloaded package failed " + path + ": " + e.Message); }
                if (found.Count >= wanted.Count) break;
            }
            foreach (string name in wanted) {
                if (found.Contains(name)) continue;
                string direct = Path.Combine(missingDepsDownloadRoot, name);
                try {
                    if (File.Exists(direct)) { UpsertDownloadedPackage(ReadPackage(direct, allRoot, true)); indexed++; }
                } catch(Exception e) { DebugLog("Fast direct index downloaded package failed " + direct + ": " + e.Message); }
            }
            all.Sort((a,b)=>string.Compare(a.uid,b.uid,StringComparison.OrdinalIgnoreCase));
            SaveCache(all);
            DebugLog("Fast indexed Hub downloads=" + indexed + "/" + wanted.Count);
        } catch(Exception e) { DebugLog("IndexDownloadedPackages FAILED: " + e.ToString()); }
        return indexed;
    }
    private void UpsertDownloadedPackage(PackageLite p) {
        if (p == null) return;
        for (int i=all.Count-1;i>=0;i--) {
            PackageLite old = all[i];
            if (old != null && (string.Equals(old.uid,p.uid,StringComparison.OrdinalIgnoreCase) || string.Equals(old.fullPath,p.fullPath,StringComparison.OrdinalIgnoreCase))) all.RemoveAt(i);
        }
        all.Add(p);
        allExact[p.uid] = p;
        string latestKey = Group(p.uid) + ".latest";
        PackageLite latest;
        if (!allLatest.TryGetValue(latestKey, out latest) || latest == null || Version(p.uid) >= Version(latest.uid)) allLatest[latestKey] = p;
    }
    private long ParseHubFileSize(string value) {
        if (string.IsNullOrEmpty(value)) return 0;
        long raw; if (long.TryParse(value.Replace(",","").Trim(), out raw)) return raw;
        Match m = Regex.Match(value, @"([0-9]+(?:\.[0-9]+)?)\s*(KB|MB|GB|B)", RegexOptions.IgnoreCase);
        if (!m.Success) return 0;
        double n; if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n)) return 0;
        string unit = m.Groups[2].Value.ToUpperInvariant();
        if (unit == "KB") n *= 1024.0; else if (unit == "MB") n *= 1024.0 * 1024.0; else if (unit == "GB") n *= 1024.0 * 1024.0 * 1024.0;
        return n > long.MaxValue ? 0 : (long)n;
    }

    private string FormatBytes(long bytes) {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        if (bytes < 1024L * 1024L * 1024L) return (bytes / (1024.0 * 1024.0)).ToString("0.0") + " MB";
        return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";
    }
    private string FormatDuration(double seconds) {
        if (seconds < 60) return Math.Max(1, (int)seconds) + "秒";
        return ((int)(seconds / 60)) + "分" + ((int)seconds % 60) + "秒";
    }
    private void SetDownloadProgress(float progress, string label) {
        downloadProgressValue = Mathf.Clamp01(progress); downloadProgressLabel = label ?? "";
        if (downloadProgressFill != null) downloadProgressFill.rectTransform.anchorMax = new Vector2(Mathf.Max(0.001f, downloadProgressValue), 1f);
        if (downloadProgressText != null) downloadProgressText.text = downloadProgressLabel;
        if (statusText != null && missingDepsDownloadRunning) statusText.text = OneLine(downloadProgressLabel, 120);
    }

    private string ParseHubDownloadInfos(string text, List<string> packages, Dictionary<string, HubDownloadInfo> result) {
        try {
            JSONNode rootNode = JSON.Parse(text);
            JSONClass pkgs = rootNode == null ? null : rootNode["packages"].AsObject;
            if (pkgs == null) return "Hub响应没有 packages 字段";
            for (int i = 0; i < packages.Count; i++) {
                string dep = packages[i];
                JSONClass item = pkgs[dep].AsObject;
                if (item == null) continue;
                HubDownloadInfo info = new HubDownloadInfo();
                info.requestName = dep;
                info.filename = JV(item["filename"]);
                info.downloadUrl = JV(item["downloadUrl"]);
                info.resourceId = JV(item["resource_id"]);
                info.fileSize = JV(item["file_size"]);
                if (!result.ContainsKey(dep)) result.Add(dep, info);
            }
            return "";
        } catch(Exception e) {
            return "Hub响应解析失败：" + e.Message + " | " + OneLine(text, 160);
        }
    }

    private delegate void ProcessDoneCallback(int exitCode, string stdout, string stderr);
    private IEnumerator RunProcessCoroutine(string exe, string args, int timeoutSec, ProcessDoneCallback callback) {
        Process pr = null;
        string stdout = "", stderr = "";
        StringBuilder stdoutBuffer = new StringBuilder(), stderrBuffer = new StringBuilder();
        int exitCode = -1;
        bool started = false, timedOut = false;
        try {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.CreateNoWindow = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            pr = new Process();
            pr.StartInfo = psi;
            pr.OutputDataReceived += (object sender, DataReceivedEventArgs e) => { if (e.Data != null) lock(stdoutBuffer) stdoutBuffer.AppendLine(e.Data); };
            pr.ErrorDataReceived += (object sender, DataReceivedEventArgs e) => { if (e.Data != null) lock(stderrBuffer) stderrBuffer.AppendLine(e.Data); };
            started = pr.Start();
            if (started) activeHubProcess = pr;
            if (started) { pr.BeginOutputReadLine(); pr.BeginErrorReadLine(); }
        } catch(Exception e) {
            stderr = e.Message;
            exitCode = -98;
        }

        if (started) {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(5, timeoutSec));
            while (pr != null && !pr.HasExited) {
                if (missingDepsDownloadCancelRequested) {
                    try { pr.Kill(); } catch {}
                    stderr = "用户取消";
                    exitCode = -97;
                    break;
                }
                if (DateTime.UtcNow > deadline) {
                    try { pr.Kill(); } catch {}
                    timedOut = true;
                    exitCode = -99;
                    break;
                }
                yield return null;
            }
            try { if (pr != null) pr.WaitForExit(2000); } catch {}
            if (!timedOut) { try { if (pr != null && pr.HasExited) exitCode = pr.ExitCode; } catch {} }
            lock(stdoutBuffer) stdout = stdoutBuffer.ToString();
            lock(stderrBuffer) stderr = stderrBuffer.ToString();
            if (timedOut) stderr = "timeout" + (stderr == "" ? "" : " | " + stderr);
        }
        if (object.ReferenceEquals(activeHubProcess, pr)) activeHubProcess = null;
        try { if (pr != null) pr.Dispose(); } catch {}
        if (callback != null) callback(exitCode, stdout, stderr);
    }

    private string ArgQ(string s) {
        if (s == null) s = "";
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    private bool IsRequestError(UnityWebRequest req) {
        if (req == null) return true;
        return req.isNetworkError || req.isHttpError;
    }

    private string SafeFileName(string filename) {
        try {
            string name = Path.GetFileName((filename ?? "").Replace('\\','/'));
            if (string.IsNullOrEmpty(name)) return "";
            char[] bad = Path.GetInvalidFileNameChars();
            for (int i = 0; i < bad.Length; i++) name = name.Replace(bad[i], '_');
            return name;
        } catch { return ""; }
    }
    private string DescribeSelectionState() {
        return "selectedPreset=" + (selectedPreset == null ? "-" : selectedPreset.fullPath)
            + " | selectedVarPreset=" + (selectedVarPreset == null ? "-" : (selectedVarPreset.package == null ? "pkg-null" : selectedVarPreset.package.uid + ":/" + selectedVarPreset.entryPath))
            + " | selectedPackage=" + (selected == null ? "-" : selected.uid)
            + " | activeCat=" + activeCat
            + " | targetAtom=" + targetAtomUid;
    }
    private string MaterializePackageEntryToTempFile(PackageLite p, string entryPath) {
        if (p == null) throw new Exception("包为空");
        if (string.IsNullOrEmpty(entryPath)) throw new Exception("包内条目为空");
        byte[] data = ReadBytes(p, entryPath, 20L * 1024L * 1024L);
        if (data == null || data.Length == 0) throw new Exception("读取包内预设失败：" + p.uid + " [" + entryPath + "]");
        string presetType = DetectPresetTypeFromPath(entryPath);
        string storeDir = GetLocalPresetStoreDir(presetType);
        string pkgDir = Path.Combine(Path.Combine(storeDir, "_AllPackagesLinkerTemp"), SafeFileName(p.uid));
        Directory.CreateDirectory(pkgDir);
        string baseName = SafeFileName(entryPath);
        if (string.IsNullOrEmpty(baseName)) baseName = "preset.vap";
        string outPath = Path.Combine(pkgDir, baseName);
        File.WriteAllBytes(outPath, data);
        DebugLog("MaterializePackageEntryToTempFile OK: " + p.uid + ":/" + entryPath + " -> " + outPath + ", bytes=" + data.Length);
        return outPath;
    }

    private string MaterializePackageScriptEntryToLocal(PackageLite p, string entryPath) {
        bool ignored;
        return MaterializePackageScriptEntryToLocal(p, entryPath, out ignored);
    }

    private string MaterializePackageScriptEntryToLocal(PackageLite p, string entryPath, out bool materializedNow) {
        materializedNow = false;
        if (p == null) throw new Exception("包为空");
        string entry = Norm(entryPath);
        if (string.IsNullOrEmpty(entry)) throw new Exception("包内脚本条目为空");
        string lower = entry.ToLowerInvariant();
        string prefix = "custom/scripts/";
        int prefixAt = lower.IndexOf(prefix, StringComparison.Ordinal);
        if (prefixAt < 0) throw new Exception("脚本不在 Custom/Scripts 下：" + entry);
        string suffix = entry.Substring(prefixAt + prefix.Length).TrimStart('/');
        if (string.IsNullOrEmpty(suffix)) throw new Exception("脚本相对路径为空：" + entry);

        string safeUid = SafeFileName(p.uid);
        if (string.IsNullOrEmpty(safeUid)) safeUid = "package";
        string tempRoot = Path.Combine(Path.Combine(vamRoot, "Custom\\Scripts\\_AllPackagesLinkerTemp"), safeUid);
        string rootKey = p.uid + "|" + p.fullPath + "|" + p.mtimeUtcTicks.ToString();
        string localFull = Path.Combine(tempRoot, suffix.Replace('/', Path.DirectorySeparatorChar));
        string markerPath = Path.Combine(tempRoot, ".apl_source.txt");
        string markerValue = p.fullPath + "|" + p.size.ToString() + "|" + p.mtimeUtcTicks.ToString();
        string cachedRoot;
        bool validOnDisk = false;
        try { validOnDisk = Directory.Exists(tempRoot) && File.Exists(localFull) && File.Exists(markerPath) && string.Equals(File.ReadAllText(markerPath), markerValue, StringComparison.Ordinal); } catch {}
        if ((!materializedScriptRoots.TryGetValue(rootKey, out cachedRoot) || !Directory.Exists(tempRoot)) && !validOnDisk) {
            SafeRecreateDirectoryUnder(Path.Combine(vamRoot, "Custom\\Scripts\\_AllPackagesLinkerTemp"), tempRoot);
            ExtractPackageScriptsToDirectory(p, tempRoot);
            File.WriteAllText(markerPath, markerValue, Encoding.UTF8);
            materializedNow = true;
        } else if (validOnDisk && string.IsNullOrEmpty(cachedRoot)) {
            DebugLog("MaterializePackageScriptEntryToLocal reused disk cache: " + p.uid + " -> " + tempRoot);
        }
        materializedScriptRoots[rootKey] = tempRoot;

        if (!File.Exists(localFull)) {
            // Some packages put the entry under a different slash/case style; extract once more and re-check.
            SafeRecreateDirectoryUnder(Path.Combine(vamRoot, "Custom\\Scripts\\_AllPackagesLinkerTemp"), tempRoot);
            ExtractPackageScriptsToDirectory(p, tempRoot);
            File.WriteAllText(markerPath, markerValue, Encoding.UTF8);
            materializedNow = true;
        }
        if (!File.Exists(localFull)) throw new Exception("本地化脚本失败：" + p.uid + ":/" + entry + " -> " + localFull);
        string localRel = "Custom/Scripts/_AllPackagesLinkerTemp/" + safeUid + "/" + suffix.Replace('\\','/');
        DebugLog("MaterializePackageScriptEntryToLocal OK: " + p.uid + ":/" + entry + " -> " + localRel);
        return localRel;
    }

    private void ExtractPackageScriptsToDirectory(PackageLite p, string tempRoot) {
        ZipFile zip = null;
        int files = 0;
        long bytes = 0;
        try {
            zip = new ZipFile(p.fullPath);
            IEnumerator en = zip.GetEnumerator();
            while (en.MoveNext()) {
                ZipEntry e = en.Current as ZipEntry;
                if (e == null || !e.IsFile) continue;
                string n = Norm(e.Name);
                string lower = n.ToLowerInvariant();
                string prefix = "custom/scripts/";
                if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string suffix = n.Substring(prefix.Length).TrimStart('/');
                if (string.IsNullOrEmpty(suffix)) continue;
                if (files > 6000) throw new Exception("脚本文件过多：" + p.uid);
                if (e.Size > 50L * 1024L * 1024L) throw new Exception("脚本文件过大：" + n);
                bytes += Math.Max(0L, e.Size);
                if (bytes > 180L * 1024L * 1024L) throw new Exception("脚本总量过大：" + p.uid);
                string outPath = Path.Combine(tempRoot, suffix.Replace('/', Path.DirectorySeparatorChar));
                string fullRoot = Path.GetFullPath(tempRoot).TrimEnd('\\','/') + Path.DirectorySeparatorChar;
                string fullOut = Path.GetFullPath(outPath);
                if (!fullOut.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(fullOut));
                using (Stream input = zip.GetInputStream(e))
                using (FileStream output = File.Open(fullOut, FileMode.Create, FileAccess.Write, FileShare.Read)) {
                    byte[] buf = new byte[32768];
                    int r;
                    while ((r = input.Read(buf, 0, buf.Length)) > 0) output.Write(buf, 0, r);
                }
                try { File.SetLastWriteTimeUtc(fullOut, new DateTime(Math.Max(0L, p.mtimeUtcTicks), DateTimeKind.Utc)); } catch {}
                files++;
            }
            if (files == 0) throw new Exception("包内没有 Custom/Scripts 文件：" + p.uid);
            DebugLog("ExtractPackageScriptsToDirectory OK: " + p.uid + ", files=" + files + ", bytes=" + bytes + ", dir=" + tempRoot);
        } finally {
            if (zip != null) zip.Close();
        }
    }

    private void SafeRecreateDirectoryUnder(string allowedRoot, string targetDir) {
        string rootFull = Path.GetFullPath(allowedRoot).TrimEnd('\\','/') + Path.DirectorySeparatorChar;
        string targetFull = Path.GetFullPath(targetDir).TrimEnd('\\','/');
        if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) throw new Exception("拒绝清理非临时目录：" + targetDir);
        if (Directory.Exists(targetFull)) Directory.Delete(targetFull, true);
        Directory.CreateDirectory(targetFull);
    }

    private bool TryResolvePackageByUid(string uid, out PackageLite p, out string source) {
        p = null;
        source = "";
        if (string.IsNullOrEmpty(uid)) return false;
        if (TryGetAvailableAddonPackage(uid, out p, out source)) return true;
        bool already = false;
        if (TryResolveDepDetailed(uid, out p, out already, out source)) return true;
        string latestKey = Group(uid) + ".latest";
        if (allLatest.TryGetValue(latestKey, out p) && CanOpenVarFile(p.fullPath)) { source = "all-latest-fallback"; return true; }
        if (FindValidLatestInAll(latestKey, out p)) { source = "all-latest-fallback-scan"; return true; }
        for (int i = 0; i < all.Count; i++) {
            PackageLite x = all[i];
            if (x != null && string.Equals(x.uid, uid, StringComparison.OrdinalIgnoreCase) && CanOpenVarFile(x.fullPath)) {
                p = x;
                source = "all-linear";
                return true;
            }
        }
        return false;
    }

    private string MaterializeSceneWithLocalScripts(PackageLite scenePackage, string sceneEntry, string sceneJson, out int replaced, out List<string> errors, out bool scriptsChanged) {
        replaced = 0;
        scriptsChanged = false;
        errors = new List<string>();
        if (string.IsNullOrEmpty(sceneJson)) return "";
        Stopwatch scanSw = Stopwatch.StartNew();
        List<SceneScriptRefOccurrence> occurrences = FindSceneScriptRefs(sceneJson);
        scanSw.Stop();
        Stopwatch materializeSw = Stopwatch.StartNew();
        Dictionary<string, string> replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PackageLite> resolvedScriptPackages = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> resolvedScriptSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> missingScriptPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> attemptedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < occurrences.Count; i++) {
            SceneScriptRefOccurrence occurrence = occurrences[i];
            string fullRef = occurrence.fullRef;
            if (!attemptedRefs.Add(fullRef)) continue;
            PackageLite scriptPackage;
            string source;
            if (!resolvedScriptPackages.TryGetValue(occurrence.uid, out scriptPackage)) {
                if (missingScriptPackages.Contains(occurrence.uid)) continue;
                if (!TryResolvePackageByUid(occurrence.uid, out scriptPackage, out source)) {
                    missingScriptPackages.Add(occurrence.uid);
                    errors.Add("脚本包未找到：" + occurrence.uid);
                    continue;
                }
                resolvedScriptPackages[occurrence.uid] = scriptPackage;
                resolvedScriptSources[occurrence.uid] = source;
            } else {
                resolvedScriptSources.TryGetValue(occurrence.uid, out source);
            }
            try {
                bool materializedNow;
                string localRef = MaterializePackageScriptEntryToLocal(scriptPackage, "Custom/Scripts/" + occurrence.scriptSuffix, out materializedNow);
                if (materializedNow) scriptsChanged = true;
                replacements[fullRef] = localRef;
                DebugLog("Scene script ref localized: " + fullRef + " -> " + localRef + " via " + source);
            } catch(Exception e) {
                errors.Add(occurrence.uid + ":/" + occurrence.scriptSuffix + " -> " + e.Message);
            }
        }
        materializeSw.Stop();
        bool hasSelfRefs = scenePackage != null && !string.IsNullOrEmpty(scenePackage.uid)
            && sceneJson.IndexOf("SELF:/", StringComparison.OrdinalIgnoreCase) >= 0;
        if (replacements.Count == 0 && !hasSelfRefs) {
            DebugLog("MaterializeSceneWithLocalScripts skipped. scanMs=" + scanSw.Elapsed.TotalMilliseconds.ToString("0") + ", refs=" + occurrences.Count);
            return "";
        }
        Stopwatch rewriteSw = Stopwatch.StartNew();
        int selfRefs = 0;
        string selfReplacement = hasSelfRefs ? scenePackage.uid + ":/" : null;
        string rewritten = RewriteSceneReferences(sceneJson, occurrences, replacements, selfReplacement, out selfRefs);
        rewriteSw.Stop();
        replaced = replacements.Count + selfRefs;
        string dir = Path.Combine(vamRoot, "Saves\\scene\\_AllPackagesLinkerTempScenes");
        Directory.CreateDirectory(dir);
        string name = SafeFileName((scenePackage == null ? "scene" : scenePackage.uid) + "__" + Path.GetFileName(sceneEntry));
        if (string.IsNullOrEmpty(name)) name = "scene.json";
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name += ".json";
        string outPath = Path.Combine(dir, name);
        Stopwatch writeSw = Stopwatch.StartNew();
        File.WriteAllText(outPath, rewritten, Encoding.UTF8);
        writeSw.Stop();
        string rel = Norm(MakeRel(vamRoot, outPath));
        DebugLog("MaterializeSceneWithLocalScripts OK: scene=" + (scenePackage == null ? "" : scenePackage.uid) + ":/" + sceneEntry + ", scriptRefs=" + replacements.Count + ", selfRefs=" + selfRefs + ", replaced=" + replaced + ", out=" + rel + ", errors=" + errors.Count + ", scanMs=" + scanSw.Elapsed.TotalMilliseconds.ToString("0") + ", materializeMs=" + materializeSw.Elapsed.TotalMilliseconds.ToString("0") + ", rewriteMs=" + rewriteSw.Elapsed.TotalMilliseconds.ToString("0") + ", writeMs=" + writeSw.Elapsed.TotalMilliseconds.ToString("0"));
        return rel;
    }

    private List<SceneScriptRefOccurrence> FindSceneScriptRefs(string sceneJson) {
        const string marker = ":/Custom/Scripts/";
        List<SceneScriptRefOccurrence> result = new List<SceneScriptRefOccurrence>();
        int searchAt = 0;
        while (searchAt < sceneJson.Length) {
            int markerAt = sceneJson.IndexOf(marker, searchAt, StringComparison.OrdinalIgnoreCase);
            if (markerAt < 0) break;
            int start = markerAt - 1;
            while (start >= 0 && sceneJson[start] != '"' && sceneJson[start] != '\r' && sceneJson[start] != '\n') start--;
            start++;
            int end = markerAt + marker.Length;
            while (end < sceneJson.Length && sceneJson[end] != '"' && sceneJson[end] != '\r' && sceneJson[end] != '\n') end++;
            string uid = start < markerAt ? sceneJson.Substring(start, markerAt - start).Trim() : "";
            string scriptSuffix = end > markerAt + marker.Length ? sceneJson.Substring(markerAt + marker.Length, end - markerAt - marker.Length).Trim() : "";
            int dots = 0;
            for (int d = 0; d < uid.Length; d++) if (uid[d] == '.') dots++;
            if (dots >= 2 && uid.Length > 0 && scriptSuffix.Length > 0) {
                SceneScriptRefOccurrence occurrence = new SceneScriptRefOccurrence();
                occurrence.start = start;
                occurrence.length = end - start;
                occurrence.fullRef = sceneJson.Substring(start, end - start);
                occurrence.uid = uid;
                occurrence.scriptSuffix = scriptSuffix;
                result.Add(occurrence);
            }
            searchAt = Math.Max(end, markerAt + marker.Length);
        }
        return result;
    }

    private string RewriteSceneReferences(string sceneJson, List<SceneScriptRefOccurrence> occurrences, Dictionary<string, string> replacements, string selfReplacement, out int selfRefs) {
        StringBuilder sb = new StringBuilder(sceneJson.Length);
        selfRefs = 0;
        int cursor = 0;
        for (int i = 0; i < occurrences.Count; i++) {
            SceneScriptRefOccurrence occurrence = occurrences[i];
            if (occurrence.start < cursor) continue;
            AppendSceneSegment(sb, sceneJson, cursor, occurrence.start, selfReplacement, ref selfRefs);
            string localRef;
            if (replacements.TryGetValue(occurrence.fullRef, out localRef)) sb.Append(localRef);
            else sb.Append(sceneJson, occurrence.start, occurrence.length);
            cursor = occurrence.start + occurrence.length;
        }
        AppendSceneSegment(sb, sceneJson, cursor, sceneJson.Length, selfReplacement, ref selfRefs);
        return sb.ToString();
    }

    private void AppendSceneSegment(StringBuilder sb, string source, int start, int end, string selfReplacement, ref int selfRefs) {
        if (start >= end) return;
        if (string.IsNullOrEmpty(selfReplacement)) {
            sb.Append(source, start, end - start);
            return;
        }
        const string selfMarker = "SELF:/";
        int cursor = start;
        while (cursor < end) {
            int found = source.IndexOf(selfMarker, cursor, end - cursor, StringComparison.OrdinalIgnoreCase);
            if (found < 0) break;
            sb.Append(source, cursor, found - cursor);
            sb.Append(selfReplacement);
            selfRefs++;
            cursor = found + selfMarker.Length;
        }
        sb.Append(source, cursor, end - cursor);
    }

    private string GetLocalPresetStoreDir(string presetType) {
        string t = presetType ?? "";
        if (t == "Animation") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\AnimationPresets");
        if (t == "BreastPhysics") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\BreastPhysics");
        if (t == "Clothing") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Clothing");
        if (t == "Hair") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Hair");
        if (t == "Morphs") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Morphs");
        if (t == "Plugins") return Path.Combine(vamRoot, "Custom\\PluginPresets");
        if (t == "Pose") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Pose");
        if (t == "Skin") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Skin");
        if (t == "General") return Path.Combine(vamRoot, "Custom\\Atom\\Person\\General");
        if (t == "Full") return Path.Combine(vamRoot, "Saves\\Person\\Full");
        return Path.Combine(vamRoot, "Custom\\Atom\\Person\\Appearance");
    }
    private void OpenPanel() {
        DebugLog("OpenPanel begin. scanned=" + scanned + ", scanning=" + scanning + ", cachePackages=" + all.Count);
        try {
            if (openedViaVR && !CanPlaceVrCanvas()) {
                ScheduleVrOpenRetry("VR view/SuperController not ready");
                return;
            }
            CancelInvoke("RetryOpenPanelWhenVrReady");
            pendingVrOpenRetries = 0;
            vrCanvasWaitingForPlacement = false;
            if (!scanned && !scanning) ScanPackages();
            BuildPanel();
            ClearSelectionKeepPreview(true);
            RefreshList();
            DebugLog("OpenPanel end OK. canvas=" + (canvas != null) + ", root=" + (root != null) + ", listContent=" + (listContent != null));
        } catch(Exception e) {
            DebugLog("OpenPanel FAILED: " + e.ToString());
            Logger.LogError(e);
            // BuildPanel can fail after creating the Canvas. Tear down the
            // partial UI so the next hotkey press can retry cleanly.
            try { ClosePanel(); } catch {}
            SetStatus("打开菜单失败：" + e.Message, true);
        }
    }
    private void ClosePanel() {
        DebugLog("ClosePanel begin.");
        CancelInvoke("RetryOpenPanelWhenVrReady");
        pendingVrOpenRetries = 0;
        vrCanvasWaitingForPlacement = false;
        StopThumbLoadCoroutine();
        StopScenePrewarm(true);
        ClearPreview();
        ClearListThumbs();
        if (authorDropdownRoot != null) Destroy(authorDropdownRoot);
        if (canvas != null && SuperController.singleton != null) { try { SuperController.singleton.RemoveCanvas(canvas); } catch {} }
        if (root != null) Destroy(root);
        canvas=null; root=null; confirmRoot=null; subBarRoot=null; pageStripRoot=null; authorDropdownRoot=null; listContent=null; authorDropdownContent=null; header=null; details=null; preview=null; statusText=null; downloadProgressText=null; downloadProgressFill=null; hubDownloadButton=null; applyClothingToggle=null; applyHairToggle=null; searchInput=null; authorDropdownSearchInput=null; atomSelectorLabel=null; scenePrimaryPersonLabel=null;
        navRoot=null; settingsDrawerRoot=null; settingsBackdropRoot=null; emptyStateRoot=null;
        atomRowRoot=null; presetOptionsRoot=null; presetModeRoot=null; presetActionRoot=null; sceneModeRoot=null; scenePersonRoot=null; sceneActionRoot=null; linkActionRoot=null; hubRowRoot=null; hubDownloadRoot=null; progressSectionRoot=null; dangerRowRoot=null; moreActionsRoot=null;
        resultCountText=null; pageInfoText=null; searchPlaceholderText=null; cacheSizeText=null;
        settingsBtn=null; rescanTopBtn=null; searchClearBtn=null; clearNonEssentialCacheBtn=null; clearAllCacheBtn=null;
        loadSceneBtn=null; loadDeferredSceneBtn=null; sceneFullModeBtn=null; scenePrimaryModeBtn=null; sceneMinimalModeBtn=null; applyPresetBtn=null; loadScriptBtn=null; linkOnlyBtn=null; defaultKeepBtn=null; favToggleBtn=null;
        settingsDrawerOpen=false;
        selectedPreset=null; selectedVarPreset=null; selectedSceneItem=null; selectedWearableItem=null; selected=null;
        selectedSceneAnalysis=null;
        tabBgs.Clear();
        favSubBtns.Clear();
        if (isVRMode && pageSizeBeforeVr > 0) { pageSize = pageSizeBeforeVr; pageSizeBeforeVr = 0; SaveConfig(); }
        DebugLog("ClosePanel end.");
    }

    private void UpdateTabHighlights() {
        if (tabCats == null) return;
        for (int i = 0; i < tabBgs.Count && i < tabCats.Length; i++) {
            if (tabBgs[i] == null) continue;
            bool on = tabCats[i] == activeCat;
            tabBgs[i].color = on ? colAccentDim : new Color(0, 0, 0, 0.02f);
            Text label = tabBgs[i].GetComponentInChildren<Text>();
            if (label != null) label.color = on ? colTextPrimary : colTextSecondary;
            Transform accent = tabBgs[i].transform.Find("Accent");
            if (accent != null) {
                Image bar = accent.GetComponent<Image>();
                if (bar != null) bar.color = on ? colAccent : new Color(0, 0, 0, 0);
            }
        }
    }

    private void LoadConfig() {
        try {
            if (!File.Exists(configPath)) return;
            string[] lines = File.ReadAllLines(configPath);
            for (int i=0;i<lines.Length;i++) {
                string line=lines[i]; int eq=line.IndexOf('='); if(eq<0) continue;
                string k=line.Substring(0,eq).Trim(); string v=line.Substring(eq+1).Trim(); float f; int iv;
                if(k=="uiScale" && float.TryParse(v,out f)) uiScale=Mathf.Clamp(f,0.00045f,0.00140f);
                if(k=="uiDistance" && float.TryParse(v,out f)) uiDistance=Mathf.Clamp(f,0.80f,2.00f);
                if(k=="uiYOffset" && float.TryParse(v,out f)) uiYOffset=Mathf.Clamp(f,-0.40f,0.30f);
                if(k=="pageSize" && int.TryParse(v,out iv)) pageSize=Mathf.Clamp(iv,8,500);
                if(k=="page" && int.TryParse(v,out iv)) page=Mathf.Max(0, iv);
                if(k=="authorFilter" && !string.IsNullOrEmpty(v)) authorFilter=v;
                if(k.StartsWith("tabPage:", StringComparison.OrdinalIgnoreCase) && int.TryParse(v,out iv)) tabPages[k.Substring(8)] = Mathf.Max(0, iv);
                if(k=="autoOpenPanelInEditMode") autoOpenPanelInEditMode=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="autoOpenPanelOnPluginLoad") autoOpenPanelOnPluginLoad=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="autoOpenTargetAtomPluginPanel") autoOpenTargetAtomPluginPanel=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="autoAllowAllPlugins") autoAllowAllPlugins=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="scanAllPackagesOnStartup") scanAllPackagesOnStartup=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="autoCleanLinksBeforeSceneLoad") autoCleanLinksBeforeSceneLoad=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="sceneTexturePrewarmEnabled") sceneTexturePrewarmEnabled=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="lazyDisabledCuaEnabled") lazyDisabledCuaEnabled=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="textureFinishGear" && int.TryParse(v,out iv)) textureFinishGear=Mathf.Clamp(iv,0,3);
                if(k=="assetCallbackGear" && int.TryParse(v,out iv)) assetCallbackGear=Mathf.Clamp(iv,0,3);
                if(k=="sceneLoadMode" && int.TryParse(v,out iv)) sceneLoadMode=Mathf.Clamp(iv,0,2);
                if(k=="missingDepsDownloadRoot" && !string.IsNullOrEmpty(v)) missingDepsDownloadRoot=v;
                if(k=="vrRotationEnabled") vrRotationEnabled=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="vrRotationSensitivity" && float.TryParse(v,out f)) vrRotationSensitivity=Mathf.Clamp(f,10f,180f);
                if(k=="vrHeightSpeed" && float.TryParse(v,out f)) vrHeightSpeed=Mathf.Clamp(f,0.10f,3.00f);
                if(k=="vrRotationDeadzone" && float.TryParse(v,out f)) vrRotationDeadzone=Mathf.Clamp(f,0.05f,0.50f);
                if(k=="vrRotationInvert") vrRotationInvert=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="vrHeightInvert") vrHeightInvert=(v=="1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                if(k=="vrRotationSnapAngle" && float.TryParse(v,out f)) vrRotationSnapAngle=Mathf.Clamp(f,0f,90f);
                if(k=="vrRotationSmoothing" && float.TryParse(v,out f)) vrRotationSmoothing=Mathf.Clamp(f,0f,0.40f);
            }
        } catch(Exception e) { Logger.LogWarning("LoadConfig failed: "+e.Message); }
    }
    private void SaveConfig() {
        try {
            Directory.CreateDirectory(dataRoot);
            StringBuilder sb = new StringBuilder();
            sb.Append("uiScale=").Append(uiScale.ToString("R")).Append('\n');
            sb.Append("uiDistance=").Append(uiDistance.ToString("R")).Append('\n');
            sb.Append("uiYOffset=").Append(uiYOffset.ToString("R")).Append('\n');
            sb.Append("pageSize=").Append(pageSize).Append('\n');
            sb.Append("page=").Append(page).Append('\n');
            sb.Append("authorFilter=").Append(authorFilter ?? "All").Append('\n');
            List<string> keys = new List<string>(tabPages.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < keys.Count; i++) sb.Append("tabPage:").Append(keys[i]).Append('=').Append(Mathf.Max(0, tabPages[keys[i]])).Append('\n');
            sb.Append("autoOpenPanelInEditMode=").Append(autoOpenPanelInEditMode?"1":"0").Append('\n');
            sb.Append("autoOpenPanelOnPluginLoad=").Append(autoOpenPanelOnPluginLoad?"1":"0").Append('\n');
            sb.Append("autoOpenTargetAtomPluginPanel=").Append(autoOpenTargetAtomPluginPanel?"1":"0").Append('\n');
            sb.Append("autoAllowAllPlugins=").Append(autoAllowAllPlugins?"1":"0").Append('\n');
            sb.Append("scanAllPackagesOnStartup=").Append(scanAllPackagesOnStartup?"1":"0").Append('\n');
            sb.Append("autoCleanLinksBeforeSceneLoad=").Append(autoCleanLinksBeforeSceneLoad?"1":"0").Append('\n');
            sb.Append("sceneTexturePrewarmEnabled=").Append(sceneTexturePrewarmEnabled?"1":"0").Append('\n');
            sb.Append("lazyDisabledCuaEnabled=").Append(lazyDisabledCuaEnabled?"1":"0").Append('\n');
            sb.Append("textureFinishGear=").Append(textureFinishGear).Append('\n');
            sb.Append("assetCallbackGear=").Append(assetCallbackGear).Append('\n');
            sb.Append("sceneLoadMode=").Append(sceneLoadMode).Append('\n');
            sb.Append("missingDepsDownloadRoot=").Append(missingDepsDownloadRoot).Append('\n');
            sb.Append("vrRotationEnabled=").Append(vrRotationEnabled?"1":"0").Append('\n');
            sb.Append("vrRotationSensitivity=").Append(vrRotationSensitivity.ToString("R")).Append('\n');
            sb.Append("vrHeightSpeed=").Append(vrHeightSpeed.ToString("R")).Append('\n');
            sb.Append("vrRotationDeadzone=").Append(vrRotationDeadzone.ToString("R")).Append('\n');
            sb.Append("vrRotationInvert=").Append(vrRotationInvert?"1":"0").Append('\n');
            sb.Append("vrHeightInvert=").Append(vrHeightInvert?"1":"0").Append('\n');
            sb.Append("vrRotationSnapAngle=").Append(vrRotationSnapAngle.ToString("R")).Append('\n');
            sb.Append("vrRotationSmoothing=").Append(vrRotationSmoothing.ToString("R")).Append('\n');
            string tmp = configPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
            if (File.Exists(configPath)) File.Delete(configPath);
            File.Move(tmp, configPath);
        } catch(Exception e) { Logger.LogWarning("SaveConfig failed: "+e.Message); }
    }

    private string ResolveLinkedLibraryDownloadRoot(string configured) {
        try {
            string configuredFull = Path.GetFullPath(configured ?? "").TrimEnd('\\','/');
            string linked = Path.Combine(allRoot, "E_Vam");
            if (Directory.Exists(linked) && IsReparsePointPath(linked)) {
                // The current setup is Allpackages\E_Vam -> E:\Vam.  Downloading
                // through this path is physically the same target, but keeps the
                // indexed relPath inside allRoot so generated links stay valid.
                string targetHint = Path.GetFullPath(linked).TrimEnd('\\','/');
                if (configuredFull.Equals(@"E:\Vam", StringComparison.OrdinalIgnoreCase)
                    || configuredFull.Equals(@"E:\VAM", StringComparison.OrdinalIgnoreCase)
                    || configuredFull.Equals(targetHint, StringComparison.OrdinalIgnoreCase)) return linked;
            }
        } catch(Exception e) { DebugLog("ResolveLinkedLibraryDownloadRoot failed: " + e.Message); }
        return configured;
    }
    private void LoadMarks() {
        favoriteScenes = LoadSet(favoritesPath);
        favoriteUids = LoadSet(favoriteUidsPath);
        favoritePresets = LoadSet(favoritePresetsPath);
        defaultUids = LoadSet(defaultsPath);
        DebugLog("Marks loaded. favorites="+favoriteScenes.Count+", favoriteUids="+favoriteUids.Count+", favoritePresets="+favoritePresets.Count+", defaults="+defaultUids.Count);
    }
    private void SaveMarks() {
        SaveSet(favoriteScenes, favoritesPath);
        SaveSet(favoriteUids, favoriteUidsPath);
        SaveSet(favoritePresets, favoritePresetsPath);
        SaveSet(defaultUids, defaultsPath);
    }
    private HashSet<string> LoadSet(string path) {
        HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try {
            if(File.Exists(path)) {
                string[] lines=File.ReadAllLines(path,Encoding.UTF8);
                for(int i=0;i<lines.Length;i++){ string s=lines[i].Trim(); if(s!="" && !s.StartsWith("#")) set.Add(s); }
                return set;
            }
            string bak = path + ".bak";
            if(File.Exists(bak)) {
                string[] lines=File.ReadAllLines(bak,Encoding.UTF8);
                for(int i=0;i<lines.Length;i++){ string s=lines[i].Trim(); if(s!="" && !s.StartsWith("#")) set.Add(s); }
                DebugLog("LoadSet recovered from backup: " + bak + ", count=" + set.Count);
            }
        } catch(Exception e){
            Logger.LogWarning("LoadSet failed: "+e.Message);
            try {
                set.Clear();
                string bak = path + ".bak";
                if(File.Exists(bak)) {
                    string[] lines=File.ReadAllLines(bak,Encoding.UTF8);
                    for(int i=0;i<lines.Length;i++){ string s=lines[i].Trim(); if(s!="" && !s.StartsWith("#")) set.Add(s); }
                    DebugLog("LoadSet recovered after failure: " + bak + ", count=" + set.Count);
                }
            } catch {}
        }
        return set;
    }
    private void SaveSet(HashSet<string> set, string path) {
        try {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            List<string> lines=new List<string>(); foreach(string s in set) if(!string.IsNullOrEmpty(s)) lines.Add(s);
            lines.Sort(StringComparer.OrdinalIgnoreCase);
            string tmp = path + ".tmp";
            string bak = path + ".bak";
            File.WriteAllLines(tmp,lines.ToArray(),Encoding.UTF8);
            if (File.Exists(path)) {
                try { File.Copy(path, bak, true); } catch {}
                try { File.Replace(tmp, path, bak, true); }
                catch {
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
            } else {
                File.Move(tmp, path);
            }
            DebugLog("SaveSet OK: " + Path.GetFileName(path) + ", count=" + lines.Count);
        } catch(Exception e){ Logger.LogWarning("SaveSet failed: "+e.Message); }
    }
    private string CurrentPageKey() { return activeCat == "Favorites" ? ("Favorites|" + favSubCat) : activeCat; }
    private void SaveCurrentPageState() { tabPages[CurrentPageKey()] = Mathf.Max(0, page); SaveConfig(); }
    private void RestorePageForCurrentTab() { int p; if (tabPages.TryGetValue(CurrentPageKey(), out p)) page = Mathf.Max(0, p); else page = 0; }
    private void ChangePage(int delta) { page += delta; SaveCurrentPageState(); RefreshList(); }
    private void ChangePageBig(int direction) { ChangePage(direction * 100); }
    private void SetPageAbsolute(int p) { page = Mathf.Max(0, p); SaveCurrentPageState(); RefreshList(); }
    private int GetCurrentMaxPage() {
        if (activeCat == "Presets" || activeCat == "Clothing" || activeCat == "Hair" || activeCat == "Morphs" || (activeCat == "Favorites" && favSubCat == "Presets")) {
            List<PresetLite> pl=FilteredPresets(); List<VarPresetLite> vpl=FilteredVarPresets(); int total=pl.Count+vpl.Count;
            return total==0?0:(total-1)/pageSize;
        }
        if (activeCat=="Scenes" || (activeCat=="Favorites" && favSubCat=="Scenes")) {
            List<SceneLite> sl=FilteredScenes(); int total=sl.Count;
            return total==0?0:(total-1)/pageSize;
        }
        List<PackageLite> l=Filtered();
        return l.Count==0?0:(l.Count-1)/pageSize;
    }
    private int MaxPageSizeForMode() { return isVRMode ? 64 : 500; }
    private void ChangePageSize(int delta) { pageSize=Mathf.Clamp(pageSize+delta,8,MaxPageSizeForMode()); page=0; SaveCurrentPageState(); RefreshList(); SetStatus("每页数量="+pageSize+(isVRMode?"（VR最多64，避免掉帧）":""),false); }
    private void SetPageSize(int n) { pageSize=Mathf.Clamp(n,8,MaxPageSizeForMode()); page=0; SaveCurrentPageState(); RefreshList(); SetStatus("每页数量="+pageSize,false); }
    private Transform GetViewTransform() {
        try {
            if(SuperController.singleton!=null && SuperController.singleton.centerCameraTarget!=null) return SuperController.singleton.centerCameraTarget.transform;
        } catch {}
        try { if(Camera.main!=null) return Camera.main.transform; } catch {}
        return null;
    }

    private bool CanPlaceVrCanvas() {
        return GetViewTransform() != null;
    }

    private bool IsVrSessionActive() {
        try {
            CVRSystem sys = OpenVR.System;
            if (sys != null) {
                try { if (sys.IsTrackedDeviceConnected(OpenVR.k_unTrackedDeviceIndex_Hmd)) return true; } catch {}
                uint left = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                uint right = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.RightHand);
                if (left != OpenVR.k_unTrackedDeviceIndexInvalid || right != OpenVR.k_unTrackedDeviceIndexInvalid) return true;
            }
        } catch {}
        try {
            if (SteamBool(SteamVR_Actions.default_HeadsetOnHead, SteamVR_Input_Sources.Head)) return true;
            if (SteamBool(SteamVR_Actions.default_HeadsetOnHead, SteamVR_Input_Sources.Any)) return true;
        } catch {}
        return false;
    }

    private void ExitVrRotationMode(string reason) {
        if (vrRotationModeActive) DebugLog("VR rotation mode exit: " + reason);
        vrRotationModeActive = false;
        leftStickClickHeldLastFrame = false;
        vrRotationFilteredX = 0f;
        vrRotationFilteredY = 0f;
        pendingVrYaw = 0f;
        pendingVrHeight = 0f;
        vrSnapArmed = true;
    }

    private Transform GetNavigationRigTransform() {
        try {
            if (SuperController.singleton == null) return null;
            if (SuperController.singleton.navigationRig != null) return SuperController.singleton.navigationRig;
            if (SuperController.singleton.navigationRigParent != null) return SuperController.singleton.navigationRigParent;
            if (SuperController.singleton.navigationPlayer != null) return SuperController.singleton.navigationPlayer;
        } catch {}
        return null;
    }

    private Vector3 GetVrRotationPivot() {
        try {
            if (SuperController.singleton != null) {
                if (SuperController.singleton.navigationCamera != null) return SuperController.singleton.navigationCamera.position;
                if (SuperController.singleton.centerCameraTarget != null) return SuperController.singleton.centerCameraTarget.transform.position;
            }
        } catch {}
        Transform view = GetViewTransform();
        if (view != null) return view.position;
        Transform rig = GetNavigationRigTransform();
        return rig != null ? rig.position : Vector3.zero;
    }

    private bool ShouldPauseVrRotation() {
        try {
            if (!vrRotationEnabled) return true;
            if (!IsVrSessionActive()) return true;
            SuperController sc = SuperController.singleton;
            if (sc == null) return true;
            if (sc.isLoading) return true;
            if (sc.navigationDisabled) return true;
            if (sc.disableAllNavigation) return true;
            // Grab-navigate writes the same navigationRig; pause while stick-click is held
            // after mode already toggled so we don't fight VaM grab navigation.
            if (!sc.disableGrabNavigation && leftStickClickHeldLastFrame) return true;
        } catch { return true; }
        return GetNavigationRigTransform() == null;
    }

    private bool TryReadVrRotationInput(out bool togglePressed, out float stickX, out float stickY, out string sourceName) {
        togglePressed = false;
        stickX = 0f;
        stickY = 0f;
        sourceName = "none";

        bool steamOk = false, steamClick = false, steamDown = false;
        float steamX = 0f, steamY = 0f;
        try {
            steamClick = SteamBool(SteamVR_Actions.default_GrabNavigate, SteamVR_Input_Sources.LeftHand);
            steamDown = SteamBoolDown(SteamVR_Actions.default_GrabNavigate, SteamVR_Input_Sources.LeftHand);
            Vector2 axis = Vector2.zero;
            bool gotAxis = false;
            try {
                if (SuperController.singleton != null && SuperController.singleton.freeMoveAction != null) {
                    axis = SuperController.singleton.freeMoveAction.GetAxis(SteamVR_Input_Sources.LeftHand);
                    gotAxis = true;
                }
            } catch {}
            if (!gotAxis) axis = SteamAxis(SteamVR_Actions.default_FreeMove, SteamVR_Input_Sources.LeftHand);
            steamX = axis.x;
            steamY = axis.y;
            steamOk = true;
        } catch {}

        bool openOk = false, openClick = false;
        float openX = 0f, openY = 0f;
        try {
            CVRSystem sys = OpenVR.System;
            if (sys != null) {
                uint left = sys.GetTrackedDeviceIndexForControllerRole(ETrackedControllerRole.LeftHand);
                if (left != OpenVR.k_unTrackedDeviceIndexInvalid) {
                    VRControllerState_t ls = new VRControllerState_t();
                    if (sys.GetControllerState(left, ref ls, (uint)Marshal.SizeOf(typeof(VRControllerState_t)))) {
                        openClick = RawButton(ls.ulButtonPressed, EVRButtonId.k_EButton_Axis0);
                        openX = ls.rAxis0.x;
                        openY = ls.rAxis0.y;
                        // Some devices expose stick on Axis2; take the stronger pair.
                        float a0 = openX * openX + openY * openY;
                        float a2 = ls.rAxis2.x * ls.rAxis2.x + ls.rAxis2.y * ls.rAxis2.y;
                        if (a2 > a0) { openX = ls.rAxis2.x; openY = ls.rAxis2.y; }
                        openOk = true;
                    }
                }
            }
        } catch {}

        if (!steamOk && !openOk) return false;

        // Click: prefer Steam GrabNavigate (VaM stick-click binding), fall back to OpenVR Axis0 pressed.
        bool click = (steamOk && steamClick) || (openOk && openClick);
        bool edge = (steamOk && steamDown) || (click && !leftStickClickHeldLastFrame);
        togglePressed = edge;
        leftStickClickHeldLastFrame = click;

        // Axis: prefer the source with larger stick magnitude.
        if (steamOk && openOk) {
            float sm = steamX * steamX + steamY * steamY;
            float om = openX * openX + openY * openY;
            if (sm >= om) { stickX = steamX; stickY = steamY; sourceName = "SteamVR"; }
            else { stickX = openX; stickY = openY; sourceName = "OpenVR"; }
            if (steamDown || steamClick) sourceName = "SteamVR";
            else if (openClick) sourceName = "OpenVR";
        } else if (steamOk) {
            stickX = steamX; stickY = steamY; sourceName = "SteamVR";
        } else {
            stickX = openX; stickY = openY; sourceName = "OpenVR";
        }
        return true;
    }

    private float NormalizeStickAxis(float v) {
        float dz = Mathf.Clamp(vrRotationDeadzone, 0.05f, 0.50f);
        float abs = Mathf.Abs(v);
        if (abs <= dz) return 0f;
        float n = Mathf.Clamp01((abs - dz) / (1f - dz));
        return Mathf.Sign(v) * n;
    }

    private float SmoothStickAxis(float current, float target, float dt) {
        float smooth = Mathf.Clamp(vrRotationSmoothing, 0f, 0.40f);
        if (smooth <= 0.0001f) return target;
        float k = 1f - Mathf.Exp(-dt / smooth);
        return Mathf.Lerp(current, target, k);
    }

    private void UpdateVrRotationInput() {
        pendingVrYaw = 0f;
        pendingVrHeight = 0f;
        if (!vrRotationEnabled) {
            if (vrRotationModeActive) ExitVrRotationMode("disabled");
            return;
        }
        if (!IsVrSessionActive()) {
            if (vrRotationModeActive) ExitVrRotationMode("no-vr");
            return;
        }

        bool togglePressed;
        float stickX, stickY;
        string sourceName;
        if (!TryReadVrRotationInput(out togglePressed, out stickX, out stickY, out sourceName)) {
            if (vrRotationModeActive) {
                // Keep mode but stop motion if input briefly unavailable.
                vrRotationFilteredX = 0f;
                vrRotationFilteredY = 0f;
            }
            return;
        }

        if (togglePressed) {
            vrRotationModeActive = !vrRotationModeActive;
            vrRotationFilteredX = 0f;
            vrRotationFilteredY = 0f;
            vrSnapArmed = true;
            pendingVrYaw = 0f;
            pendingVrHeight = 0f;
            DebugLog("VR look mode " + (vrRotationModeActive ? "ON" : "OFF") + " via " + sourceName + " LeftStickClick");
            SetStatus("镜头模式：" + (vrRotationModeActive ? "开（左右转向 / 前后升降，再按摇杆退出）" : "关"), true);
        }

        if (!vrRotationModeActive) {
            vrRotationFilteredX = 0f;
            vrRotationFilteredY = 0f;
            return;
        }
        if (ShouldPauseVrRotation()) {
            vrRotationFilteredX = 0f;
            vrRotationFilteredY = 0f;
            return;
        }

        float targetX = NormalizeStickAxis(stickX);
        float targetY = NormalizeStickAxis(stickY);
        if (vrRotationInvert) targetX = -targetX;
        if (vrHeightInvert) targetY = -targetY;
        float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
        vrRotationFilteredX = SmoothStickAxis(vrRotationFilteredX, targetX, dt);
        vrRotationFilteredY = SmoothStickAxis(vrRotationFilteredY, targetY, dt);

        // Stick X -> horizontal yaw
        if (Mathf.Abs(vrRotationFilteredX) < 0.001f) {
            vrSnapArmed = true;
        } else {
            float snap = vrRotationSnapAngle;
            if (snap > 0.5f) {
                if (vrSnapArmed) {
                    pendingVrYaw = Mathf.Sign(vrRotationFilteredX) * snap;
                    vrSnapArmed = false;
                }
            } else {
                float sens = Mathf.Clamp(vrRotationSensitivity, 10f, 180f);
                pendingVrYaw = vrRotationFilteredX * sens * dt;
            }
        }

        // Stick Y -> vertical height (raise / lower view)
        if (Mathf.Abs(vrRotationFilteredY) >= 0.001f) {
            float hSpeed = Mathf.Clamp(vrHeightSpeed, 0.10f, 3.00f);
            pendingVrHeight = vrRotationFilteredY * hSpeed * dt;
        }

        string diag = "mode=1 src=" + sourceName
            + " x=" + stickX.ToString("0.00") + " y=" + stickY.ToString("0.00")
            + " yaw=" + pendingVrYaw.ToString("0.00")
            + " h=" + pendingVrHeight.ToString("0.000");
        if (diag != lastVrRotationDiag && Time.realtimeSinceStartup >= nextVrRotationQuietDiagAt) {
            lastVrRotationDiag = diag;
            nextVrRotationQuietDiagAt = Time.realtimeSinceStartup + 2f;
            DebugLog("VR look: " + diag);
        }
    }

    private void ApplyPendingVrYaw() {
        if (!vrRotationModeActive) return;
        if (ShouldPauseVrRotation()) { pendingVrYaw = 0f; pendingVrHeight = 0f; return; }

        if (Mathf.Abs(pendingVrYaw) >= 0.0001f) {
            Transform rig = GetNavigationRigTransform();
            if (rig != null) {
                float yaw = pendingVrYaw;
                pendingVrYaw = 0f;
                ApplyVrYawAroundHeadset(rig, yaw);
            } else pendingVrYaw = 0f;
        }

        if (Mathf.Abs(pendingVrHeight) >= 0.00001f) {
            float dh = pendingVrHeight;
            pendingVrHeight = 0f;
            ApplyVrHeightDelta(dh);
        }
    }

    private void ApplyVrYawAroundHeadset(Transform rig, float deltaYaw) {
        if (rig == null || Mathf.Abs(deltaYaw) < 0.0001f) return;
        Vector3 pivot = GetVrRotationPivot();
        Quaternion q = Quaternion.AngleAxis(deltaYaw, Vector3.up);
        Vector3 offset = rig.position - pivot;
        rig.position = pivot + q * offset;
        rig.rotation = q * rig.rotation;
    }

    private void ApplyVrHeightDelta(float deltaMeters) {
        if (Mathf.Abs(deltaMeters) < 0.00001f) return;
        try {
            SuperController sc = SuperController.singleton;
            if (sc != null) {
                // VaM-native player height offset (same as in-game height slider).
                sc.playerHeightAdjustAdjust(deltaMeters);
                return;
            }
        } catch (Exception e) {
            DebugLog("playerHeightAdjustAdjust failed: " + e.Message);
        }
        // Fallback: translate navigation rig in world up.
        try {
            Transform rig = GetNavigationRigTransform();
            if (rig != null) rig.position += Vector3.up * deltaMeters;
        } catch {}
    }

    private void ScheduleVrOpenRetry(string reason) {
        try {
            pendingVrOpenRetries++;
            if (pendingVrOpenRetries >= MaxPendingVrOpenRetries) {
                DebugLog("VR panel open retry timeout (" + reason + "). Falling back to desktop overlay.");
                openedViaVR = false;
                pendingVrOpenRetries = 0;
                CancelInvoke("RetryOpenPanelWhenVrReady");
                OpenPanel();
                return;
            }
            DebugLog("VR panel open delayed: " + reason + ". retry=" + pendingVrOpenRetries + "/" + MaxPendingVrOpenRetries + ", superController=" + (SuperController.singleton != null));
            CancelInvoke("RetryOpenPanelWhenVrReady");
            Invoke("RetryOpenPanelWhenVrReady", 0.5f);
        } catch(Exception e) {
            DebugLog("ScheduleVrOpenRetry FAILED: " + e.ToString());
            openedViaVR = false;
            pendingVrOpenRetries = 0;
            OpenPanel();
        }
    }

    private void RetryOpenPanelWhenVrReady() {
        try {
            if (canvas != null) { pendingVrOpenRetries = 0; return; }
            if (!openedViaVR) { pendingVrOpenRetries = 0; return; }
            OpenPanel();
        } catch(Exception e) {
            DebugLog("RetryOpenPanelWhenVrReady FAILED: " + e.ToString());
            openedViaVR = false;
            pendingVrOpenRetries = 0;
            OpenPanel();
        }
    }

    private string V3(Vector3 v) { return "(" + v.x.ToString("0.000") + "," + v.y.ToString("0.000") + "," + v.z.ToString("0.000") + ")"; }

    private void ApplyCanvasTransform() {
        if(root==null) return;
        if (!isVRMode) return; // Desktop mode uses ScreenSpaceOverlay, no transform needed
        root.transform.localScale = new Vector3(uiScale, uiScale, uiScale);

        Transform view = GetViewTransform();
        if(view!=null) {
            vrCanvasWaitingForPlacement = false;
            try { root.transform.SetParent(null, true); } catch {}
            Vector3 forward = view.forward; if(forward.sqrMagnitude < 0.0001f) forward = Vector3.forward; forward.Normalize();
            Vector3 up = Vector3.up;
            if (Mathf.Abs(Vector3.Dot(forward, up)) > 0.97f) up = view.up;
            if(up.sqrMagnitude < 0.0001f) up = Vector3.up; up.Normalize();
            root.transform.position = view.position + forward * uiDistance + Vector3.up * uiYOffset;
            root.transform.rotation = Quaternion.LookRotation(forward, up);
            DebugLog("Canvas VR fixed at view center. pos=" + V3(root.transform.position) + ", scale=" + uiScale.ToString("0.00000"));
        } else {
            vrCanvasWaitingForPlacement = true;
            nextVrPlacementRetryAt = Time.realtimeSinceStartup + 0.5f;
            root.transform.position = new Vector3(0f, 1.45f + uiYOffset, uiDistance);
            root.transform.rotation = Quaternion.identity;
            DebugLog("Canvas VR placement deferred: view transform is not ready yet.");
        }
    }
    private void ApplyCanvasScaleOnly() {
        if(!isVRMode) return;
        if(root!=null) root.transform.localScale = new Vector3(uiScale, uiScale, uiScale);
    }
    private void ChangeUiScale(float mul) { uiScale=Mathf.Clamp(uiScale*mul,0.00045f,0.00140f); SaveConfig(); ApplyCanvasScaleOnly(); SetStatus("VR界面缩放="+uiScale.ToString("0.00000"),false); }
    private void ChangeUiDistance(float delta) { uiDistance=Mathf.Clamp(uiDistance+delta,0.80f,2.00f); SaveConfig(); ApplyCanvasTransform(); SetStatus("VR界面距离="+uiDistance.ToString("0.00")+"米",false); }
    private void ChangeUiYOffset(float delta) { uiYOffset=Mathf.Clamp(uiYOffset+delta,-0.40f,0.30f); SaveConfig(); ApplyCanvasTransform(); SetStatus("VR界面高度偏移="+uiYOffset.ToString("0.00")+"米",false); }
    private void RecenterVrCanvas() { ApplyCanvasTransform(); SetStatus("VR界面已重新居中。",false); }

    private void ScanPackages() {
        if (scanning) { DebugLog("ScanPackages skipped: already scanning."); return; }
        scanning = true;
        DebugLog("ScanPackages begin. allRoot=" + allRoot);
        try {
            Dictionary<string, PackageLite> cache = LoadCacheMap();
            DebugLog("ScanPackages cache entries=" + cache.Count);
            all.Clear(); allExact.Clear(); allLatest.Clear(); wearableItems.Clear(); wearableIndexBuilt=false; selected=null; ClearPreview();
            int reused=0, parsed=0, removed=0, errors=0;
            HashSet<string> seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(string f in GetVarFiles(allRoot, true)) {
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }
                seenFiles.Add(full);
                try {
                    FileInfo fi = new FileInfo(full);
                    PackageLite p;
                    if (cache.TryGetValue(full, out p) && p.size == fi.Length && p.mtimeUtcTicks == fi.LastWriteTimeUtc.Ticks && (p.thumbEntry=="" || p.thumbCache=="" || File.Exists(p.thumbCache))) {
                        reused++;
                    } else {
                        p = ReadPackage(full, allRoot, true);
                        parsed++;
                    }
                    all.Add(p); AddMaps(p, allExact, allLatest);
                } catch(Exception e) { errors++; Logger.LogWarning("Index failed " + full + ": " + e.Message); }
            }
            foreach(string k in cache.Keys) if(!seenFiles.Contains(k)) removed++;
            all.Sort((a,b)=>string.Compare(a.uid,b.uid,StringComparison.OrdinalIgnoreCase));
            SaveCache(all);
            ScanAddonLightweight();
            ScanLocalPresets();
            EnsureVarPresetIndex();
            EnsureSceneIndex();
            PrelinkLocalPresetDeps();
            scanned = true;
            SetStatus("索引完成：总数="+all.Count+"，本地预设="+localPresets.Count+"，复用="+reused+"，新增/变化="+parsed+"，移除="+removed+"，错误="+errors+"。", true);
            DebugLog("ScanPackages end OK. total="+all.Count+", reused="+reused+", parsed="+parsed+", removed="+removed+", errors="+errors);
        } catch(Exception e) { DebugLog("ScanPackages FAILED: " + e.ToString()); SetStatus("扫描失败："+e.Message, true); Logger.LogError(e); }
        finally { DebugLog("ScanPackages finally. scanning=false"); scanning=false; }
    }

    private void ScanAddonLightweight() {
        DebugLog("ScanAddonLightweight begin. addonRoot=" + addonRoot);
        addonExact.Clear(); addonLatest.Clear();
        int count=0, skippedBroken=0;
        foreach(string f in GetVarFiles(addonRoot, false)) {
            try {
                if (!CanOpenVarFile(f)) { skippedBroken++; continue; }
                PackageLite p=BasicPackage(f, addonRoot); AddMaps(p, addonExact, addonLatest); count++;
            } catch {}
        }
        DebugLog("ScanAddonLightweight end. count=" + count);
        Logger.LogInfo("AddonPackages lightweight scan: " + count + " .var files, skippedBroken=" + skippedBroken + ".");
    }

    private void ScanLocalPresets() {
        DebugLog("ScanLocalPresets begin.");
        localPresets.Clear();
        string[] scanDirs = new string[] {
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Appearance"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\AnimationPresets"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\BreastPhysics"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Clothing"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Hair"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Morphs"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Plugins"),
            Path.Combine(vamRoot, "Custom\\PluginPresets"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Pose"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\Skin"),
            Path.Combine(vamRoot, "Custom\\Atom\\Person\\General"),
            Path.Combine(vamRoot, "Saves\\Person\\Appearance"),
            Path.Combine(vamRoot, "Saves\\PluginPresets"),
            Path.Combine(vamRoot, "Saves\\Person\\Pose"),
            Path.Combine(vamRoot, "Saves\\Person\\General"),
            Path.Combine(vamRoot, "Saves\\Person\\Full")
        };
        string[] presetTypes = new string[] { "Appearance", "Animation", "BreastPhysics", "Clothing", "Hair", "Morphs", "Plugins", "Plugins", "Pose", "Skin", "General", "Appearance", "Plugins", "Pose", "General", "Full" };
        for (int d = 0; d < scanDirs.Length; d++) {
            string dir = scanDirs[d];
            if (!Directory.Exists(dir)) continue;
            try {
                List<string> files = new List<string>();
                files.AddRange(Directory.GetFiles(dir, "*.vap", SearchOption.AllDirectories));
                files.AddRange(Directory.GetFiles(dir, "*.vaj", SearchOption.AllDirectories));
                for (int i = 0; i < files.Count; i++) {
                    try {
                        FileInfo fi = new FileInfo(files[i]);
                        if (fi.FullName.IndexOf("_AllPackagesLinkerTemp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        PresetLite pr = new PresetLite();
                        pr.fullPath = fi.FullName;
                        pr.name = Path.GetFileNameWithoutExtension(fi.Name);
                        if (pr.name.StartsWith("Preset_")) pr.name = pr.name.Substring(7);
                        pr.relPath = MakeRel(vamRoot, fi.FullName);
                        pr.presetType = presetTypes[d];
                        pr.size = fi.Length;
                        pr.mtimeUtcTicks = fi.LastWriteTimeUtc.Ticks;
                        localPresets.Add(pr);
                        // Detect VaM native .fav file
                        if (File.Exists(fi.FullName + ".fav")) favoritePresets.Add(fi.FullName);
                    } catch {}
                }
            } catch {}
        }
        localPresets.Sort((a, b) => {
            int c = PresetTypePriority(a.presetType).CompareTo(PresetTypePriority(b.presetType));
            if (c != 0) return c;
            return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
        });
        DebugLog("ScanLocalPresets end. count=" + localPresets.Count);
    }

    private void PrelinkLocalPresetDeps() {
        try {
            if (localPresets == null || localPresets.Count == 0) {
                DebugLog("PrelinkLocalPresetDeps skipped: no local presets.");
                return;
            }
            DebugLog("PrelinkLocalPresetDeps begin. presets=" + localPresets.Count);
            int scannedPresets = 0, linked = 0, already = 0;
            HashSet<string> missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> errors = new List<string>();
            for (int i = 0; i < localPresets.Count; i++) {
                PresetLite pr = localPresets[i];
                if (pr == null || string.IsNullOrEmpty(pr.fullPath) || !File.Exists(pr.fullPath)) continue;
                try {
                    FileInfo fi = new FileInfo(pr.fullPath);
                    if (fi.Length <= 0 || fi.Length > 20L * 1024L * 1024L) continue;
                    string json = File.ReadAllText(pr.fullPath, Encoding.UTF8);
                    PresetLinkDiag d = AutoLinkPresetDepsDetailed(json);
                    scannedPresets++;
                    linked += d.linked;
                    already += d.already;
                    for (int m = 0; m < d.missing.Count; m++) missing.Add(d.missing[m]);
                    for (int e = 0; e < d.errors.Count; e++) errors.Add(Path.GetFileName(pr.fullPath) + ":" + d.errors[e]);
                } catch(Exception e) {
                    errors.Add(Path.GetFileName(pr.fullPath) + ":" + e.Message);
                }
            }
            string missText = string.Join(",", new List<string>(missing).ToArray());
            string errText = string.Join("；", errors.ToArray());
            DebugLog("PrelinkLocalPresetDeps end. scannedPresets=" + scannedPresets + ", linked=" + linked + ", already=" + already + ", missing=" + missing.Count + (missing.Count > 0 ? " [" + missText + "]" : "") + ", errors=" + errors.Count + (errors.Count > 0 ? " [" + errText + "]" : ""));
            if (linked > 0 || missing.Count > 0 || errors.Count > 0) {
                string msg = "本地预设依赖预关联完成：预设=" + scannedPresets + "，新建链接=" + linked + "，已存在=" + already + "，缺失=" + missing.Count;
                if (missing.Count > 0) msg += "：" + missText;
                if (errors.Count > 0) msg += "，异常=" + errors.Count;
                SetStatus(msg, true);
            }
        } catch(Exception e) {
            DebugLog("PrelinkLocalPresetDeps FAILED: " + e.ToString());
        }
    }

    private PackageLite BasicPackage(string path, string scanRoot) {
        FileInfo fi = new FileInfo(path); PackageLite p = new PackageLite();
        p.fullPath=fi.FullName; p.relPath=MakeRel(scanRoot, fi.FullName); p.uid=Path.GetFileNameWithoutExtension(fi.Name); p.size=fi.Length; p.mtimeUtcTicks=fi.LastWriteTimeUtc.Ticks;
        return p;
    }

    private List<string> GetVarFiles(string start, bool followFirstLink) {
        List<string> result = new List<string>();
        var stack = new Stack<DirItem>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        stack.Push(new DirItem(start,false));
        while(stack.Count>0) {
            DirItem cur=stack.Pop(); string dir=cur.dir; bool via=cur.via;
            string full; try { full=Path.GetFullPath(dir).TrimEnd('\\','/'); } catch { continue; }
            if (!seen.Add(full) || seen.Count>20000) continue;
            string[] files=null; try { files=Directory.GetFiles(dir,"*.var",SearchOption.TopDirectoryOnly); } catch {}
            if (files!=null) foreach(string f in files) result.Add(f);
            string[] dirs=null; try { dirs=Directory.GetDirectories(dir,"*",SearchOption.TopDirectoryOnly); } catch {}
            if (dirs==null) continue;
            foreach(string d in dirs) {
                try {
                    bool rp = (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0;
                    if (rp && (!followFirstLink || via)) continue;
                    stack.Push(new DirItem(d, via || rp));
                } catch {}
            }
        }
        return result;
    }

    private PackageLite ReadPackage(string path, string scanRoot, bool cacheThumb) {
        var p = BasicPackage(path, scanRoot);
        var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string bestThumb=""; int bestPri=999;
        ZipFile zip=null;
        try {
            zip = new ZipFile(path);
            ZipEntry meta = FindEntry(zip,"meta.json");
            if (meta != null) {
                string txt = ReadText(zip, meta); JSONNode j = JSON.Parse(txt);
                if (j != null) { p.description=JV(j["description"]); if (j["contentList"]!=null) for(int i=0;i<j["contentList"].Count;i++) CatPath(JV(j["contentList"][i]), cats); AddDeps(j,deps); }
            }
            IEnumerator en = zip.GetEnumerator();
            while(en.MoveNext()) {
                ZipEntry e = en.Current as ZipEntry; if (e==null || !e.IsFile) continue;
                string n = Norm(e.Name); CatPath(n,cats);
                if (IsScene(n)) { if(!p.scenes.Contains(n)) p.scenes.Add(n); if (p.firstScene=="") p.firstScene=n; }
                if (IsPersonPresetPath(n)) {
                    string presetType = DetectPresetTypeFromPath(n);
                    string spec = MakePresetSpec(presetType, n);
                    if (!p.presetSpecs.Contains(spec)) p.presetSpecs.Add(spec);
                }
                int pri = ThumbPri(n); if (pri < bestPri) { bestPri=pri; bestThumb=n; }
            }
            p.thumbEntry=bestThumb;
            if (cacheThumb && bestThumb!="") SaveThumbCache(p, zip, bestThumb);
        } finally { if (zip!=null) zip.Close(); }
        if (cats.Count==0) cats.Add("Other"); p.cats.AddRange(cats); p.cats.Sort(); p.deps.AddRange(deps); p.deps.Sort(); p.scenes.Sort(); p.presetSpecs.Sort(); return p;
    }

    private void SaveThumbCache(PackageLite p, ZipFile zip, string entryName) {
        try {
            string ext = Path.GetExtension(entryName).ToLowerInvariant(); if(ext!=".jpg" && ext!=".jpeg" && ext!=".png") ext=".img";
            string file = Path.Combine(thumbRoot, Sha1Hex(p.fullPath+"|"+p.size+"|"+p.mtimeUtcTicks+"|"+entryName)+ext);
            p.thumbCache=file;
            if (File.Exists(file)) return;
            ZipEntry e=FindEntry(zip, entryName); if(e==null) return;
            byte[] data=ReadEntryBytes(zip,e,12L*1024L*1024L); if(data==null) return;
            File.WriteAllBytes(file,data);
        } catch(Exception e) { Logger.LogWarning("Thumb cache failed " + p.uid + ": " + e.Message); }
    }

    private string Sha1Hex(string s) { using(SHA1 sha=SHA1.Create()) { byte[] h=sha.ComputeHash(Encoding.UTF8.GetBytes(s)); StringBuilder sb=new StringBuilder(); for(int i=0;i<h.Length;i++) sb.Append(h[i].ToString("x2")); return sb.ToString(); } }
    private string JV(JSONNode n) { return n==null||n.Value==null?"":n.Value.Trim(); }
    private string Norm(string s) { return (s??"").Replace('\\','/').TrimStart('/'); }
    private bool IsScene(string n) { string p=n.ToLowerInvariant(); return p.StartsWith("saves/scene/") && p.EndsWith(".json"); }
    private bool IsPersonPresetPath(string n) {
        string p = Norm(n).ToLowerInvariant();
        bool isPresetExt = p.EndsWith(".vap") || p.EndsWith(".vaj") || p.EndsWith(".json");
        if (!isPresetExt) return false;
        if (p.StartsWith("custom/atom/person/animationpresets/")) return true;
        if (p.StartsWith("custom/atom/person/appearance/")) return true;
        if (p.StartsWith("custom/atom/person/breastphysics/")) return true;
        if (p.StartsWith("custom/atom/person/clothing/")) return true;
        if (p.StartsWith("custom/atom/person/pose/")) return true;
        if (p.StartsWith("custom/atom/person/hair/")) return true;
        if (p.StartsWith("custom/atom/person/morphs/")) return true;
        if (p.StartsWith("custom/atom/person/plugins/")) return true;
        if (p.StartsWith("custom/pluginpresets/")) return true;
        if (p.StartsWith("custom/atom/person/skin/")) return true;
        if (p.StartsWith("custom/atom/person/general/")) return true;
        if (p.StartsWith("saves/person/appearance/")) return true;
        if (p.StartsWith("saves/pluginpresets/")) return true;
        if (p.StartsWith("saves/person/pose/")) return true;
        if (p.StartsWith("saves/person/general/")) return true;
        if (p.StartsWith("saves/person/full/")) return true;
        return false;
    }
    private string MakePresetSpec(string presetType, string entryPath) { return (presetType ?? "Full") + "|" + Norm(entryPath); }
    private string PresetSpecType(string spec) { int k = spec.IndexOf('|'); return k > 0 ? spec.Substring(0, k) : "Full"; }
    private string PresetSpecPath(string spec) { int k = spec.IndexOf('|'); return k >= 0 ? spec.Substring(k + 1) : spec; }
    private void CatPath(string n, HashSet<string> c) {
        string p=Norm(n).ToLowerInvariant(); if (p=="") return;
        if (p.StartsWith("saves/scene/") && (p.EndsWith(".json")||p.EndsWith(".jpg")||p.EndsWith(".jpeg")||p.EndsWith(".png"))) c.Add("Scenes");
        if (IsPersonPresetPath(p)) { c.Add("Looks"); c.Add("Presets"); }
        if (p.StartsWith("custom/clothing/")||p.StartsWith("custom/atom/person/clothing/")) c.Add("Clothing");
        if (p.StartsWith("custom/hair/")||p.StartsWith("custom/atom/person/hair/")) c.Add("Hair");
        if (p.StartsWith("custom/atom/person/morphs/")||p.StartsWith("custom/morphs/")||p.Contains("/morphs/")||(p.EndsWith(".vmb"))||p.EndsWith(".dsf")||p.EndsWith(".vmi")) c.Add("Morphs");
        if (p.StartsWith("custom/assets/")||p.EndsWith(".assetbundle")) c.Add("Assets");
        if (p.StartsWith("custom/scripts/")||p.EndsWith(".cs")||p.EndsWith(".cslist")) { c.Add("Scripts"); c.Add("Plugins"); }
        if (p.EndsWith(".dll")||p.Contains("/plugins/")) c.Add("Plugins");
    }
    private int ThumbPri(string n) { string p=n.ToLowerInvariant(); if(!(p.EndsWith(".jpg")||p.EndsWith(".jpeg")||p.EndsWith(".png"))) return 999; if(p.StartsWith("custom/clothing/")||p.StartsWith("custom/atom/person/clothing/")) return 1; if(p.StartsWith("custom/hair/")||p.StartsWith("custom/atom/person/hair/")) return 2; if(p.Contains("appearance")||p.StartsWith("saves/person/")) return 3; if(p.StartsWith("saves/scene/")) return 5; return 10; }
    private void AddDeps(JSONNode n, HashSet<string> d) { if(n==null) return; JSONClass o=n["dependencies"].AsObject; if(o==null) return; foreach(string k in o.Keys) { if(!string.IsNullOrEmpty(k)) d.Add(k.Trim()); AddDeps(o[k],d); } }

    private ZipEntry FindEntry(ZipFile z, string name) { IEnumerator en=z.GetEnumerator(); while(en.MoveNext()) { ZipEntry e=en.Current as ZipEntry; if(e!=null && string.Equals(Norm(e.Name),name,StringComparison.OrdinalIgnoreCase)) return e; } return null; }
    private string ReadText(ZipFile z, ZipEntry e) { using(Stream s=z.GetInputStream(e)) using(StreamReader r=new StreamReader(s,Encoding.UTF8)) return r.ReadToEnd(); }
    private byte[] ReadEntryBytes(ZipFile z, ZipEntry e, long max) { if(e.Size>max) return null; using(Stream s=z.GetInputStream(e)) { MemoryStream ms=new MemoryStream(); byte[] b=new byte[8192]; int r; long total=0; while((r=s.Read(b,0,b.Length))>0) { total+=r; if(total>max) return null; ms.Write(b,0,r); } return ms.ToArray(); } }
    private byte[] ReadBytes(PackageLite p, string name, long max) { ZipFile z=null; try { z=new ZipFile(p.fullPath); ZipEntry e=FindEntry(z,name); if(e==null) return null; return ReadEntryBytes(z,e,max); } finally { if(z!=null) z.Close(); } }

    private string GetTimelineCachePath(PackageLite p, string scene) {
        if (p == null || string.IsNullOrEmpty(p.fullPath) || string.IsNullOrEmpty(dataRoot)) return "";
        FileInfo fi = new FileInfo(p.fullPath);
        string key = Path.GetFullPath(p.fullPath) + "|" + fi.Length.ToString(CultureInfo.InvariantCulture) + "|"
            + fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Norm(scene) + "|" + TimelineConverterVersion;
        return Path.Combine(Path.Combine(dataRoot, "timeline-cache"), Sha1Hex(key) + ".json");
    }

    private bool TryReadTimelineCache(PackageLite p, string scene, out string json, out string cachePath) {
        json = "";
        cachePath = GetTimelineCachePath(p, scene);
        try {
            if (string.IsNullOrEmpty(cachePath) || !File.Exists(cachePath)) return false;
            FileInfo fi = new FileInfo(cachePath);
            if (fi.Length <= 0 || fi.Length > MaxLargeSceneTextBytes) return false;
            json = File.ReadAllText(cachePath, Encoding.UTF8);
            return !string.IsNullOrEmpty(json);
        } catch(Exception e) {
            DebugLog("Timeline cache read failed: " + cachePath + " | " + e.Message);
            json = "";
            return false;
        }
    }

    private string ReadSceneText(PackageLite p, string scene, long maxBytes, out long entryBytes) {
        entryBytes = 0;
        if (p == null || string.IsNullOrEmpty(p.fullPath)) return "";
        ZipFile zip = null;
        try {
            zip = new ZipFile(p.fullPath);
            ZipEntry entry = FindEntry(zip, scene);
            if (entry == null) return "";
            entryBytes = entry.Size;
            if (entry.Size < 0 || entry.Size > maxBytes) throw new IOException("场景 JSON 超过读取上限：" + entry.Size + " > " + maxBytes);
            using(Stream input = zip.GetInputStream(entry))
            using(StreamReader reader = new StreamReader(input, Encoding.UTF8, true, 1024 * 1024)) return reader.ReadToEnd();
        } finally {
            if (zip != null) zip.Close();
        }
    }

    private string ReadSceneJsonWithTimelineOptimization(PackageLite p, string scene, out TimelineOptimizationInfo info) {
        info = new TimelineOptimizationInfo();
        info.cachePath = GetTimelineCachePath(p, scene);
        string cached;
        string cachePath;
        Stopwatch sw = Stopwatch.StartNew();
        if (TryReadTimelineCache(p, scene, out cached, out cachePath)) {
            sw.Stop();
            info.cacheHit = true;
            info.optimized = true;
            info.cacheReadMs = sw.Elapsed.TotalMilliseconds;
            info.outputBytes = new FileInfo(cachePath).Length;
            DebugLog("Timeline optimization cache HIT: uid=" + p.uid + ", scene=" + scene + ", bytes=" + info.outputBytes + ", readMs=" + info.cacheReadMs.ToString("0") + ", cache=" + cachePath);
            return cached;
        }

        sw.Reset(); sw.Start();
        long entryBytes;
        string source = ReadSceneText(p, scene, MaxLargeSceneTextBytes, out entryBytes);
        sw.Stop();
        info.readMs = sw.Elapsed.TotalMilliseconds;
        info.sourceBytes = entryBytes;
        if (string.IsNullOrEmpty(source)) return "";

        string error;
        sw.Reset(); sw.Start();
        bool optimized = TryOptimizeLegacyTimelineSceneToCache(source, info.cachePath, info, out error);
        sw.Stop();
        info.optimizeMs = sw.Elapsed.TotalMilliseconds;
        if (!optimized) {
            info.error = error;
            DebugLog("Timeline optimization skipped: uid=" + p.uid + ", scene=" + scene + ", sourceBytes=" + info.sourceBytes + ", readMs=" + info.readMs.ToString("0") + ", optimizeMs=" + info.optimizeMs.ToString("0") + ", reason=" + error);
            return source;
        }

        info.optimized = true;
        bool releaseLargeSource = source.Length > 128 * 1024 * 1024;
        source = null;
        if (releaseLargeSource) GC.Collect();
        sw.Reset(); sw.Start();
        string result = File.ReadAllText(info.cachePath, Encoding.UTF8);
        sw.Stop();
        info.cacheReadMs = sw.Elapsed.TotalMilliseconds;
        DebugLog("Timeline optimization cache MISS converted: uid=" + p.uid + ", scene=" + scene + ", sourceBytes=" + info.sourceBytes + ", outputBytes=" + info.outputBytes + ", animations=" + info.animations + ", curves=" + info.curves + ", keys=" + info.keyframes + ", readMs=" + info.readMs.ToString("0") + ", optimizeMs=" + info.optimizeMs.ToString("0") + ", cacheReadMs=" + info.cacheReadMs.ToString("0") + ", cache=" + info.cachePath);
        return result;
    }

    private static bool TryOptimizeLegacyTimelineSceneToCache(string source, string cachePath, TimelineOptimizationInfo info, out string error) {
        error = "";
        string tempPath = "";
        try {
            if (string.IsNullOrEmpty(source)) { error = "scene JSON is empty"; return false; }
            if (string.IsNullOrEmpty(cachePath)) { error = "cache path is empty"; return false; }
            SceneJsonAnalysis analysis = new SceneJsonAnalysis();
            string analysisError;
            if (!TryAnalyzeSceneAtoms(source, analysis, out analysisError)) { error = analysisError; return false; }

            List<TimelineRewriteSpan> rewrites = new List<TimelineRewriteSpan>();
            for (int atomIndex = 0; atomIndex < analysis.atoms.Count; atomIndex++) {
                SceneAtomSpan atom = analysis.atoms[atomIndex];
                TimelinePropertySpan storables = FindTimelineDirectProperty(source, atom.start, atom.start + atom.length, "storables");
                if (storables == null || storables.kind != '[') continue;
                List<TimelineObjectSpan> storablesList = ReadTimelineObjectArray(source, storables.start, storables.start + storables.length);
                for (int storableIndex = 0; storableIndex < storablesList.Count; storableIndex++) {
                    TimelineObjectSpan storable = storablesList[storableIndex];
                    string id;
                    if (!TryFindDirectStringProperty(source, storable.start, storable.start + storable.length, "id", out id)
                        || id.IndexOf("VamTimeline", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    TimelinePropertySpan animation = FindTimelineDirectProperty(source, storable.start, storable.start + storable.length, "Animation");
                    if (animation == null || animation.kind != '{') continue;
                    List<TimelinePropertySpan> animationProperties = ReadTimelineDirectProperties(source, animation.start, animation.start + animation.length);
                    if (FindTimelineProperty(animationProperties, "SerializeVersion") != null || FindTimelineProperty(animationProperties, "SerializeMode") != null) continue;
                    TimelinePropertySpan clips = FindTimelineProperty(animationProperties, "Clips");
                    if (clips == null || clips.kind != '[') continue;

                    List<TimelineRewriteSpan> animationCurves = new List<TimelineRewriteSpan>();
                    List<TimelineObjectSpan> clipObjects = ReadTimelineObjectArray(source, clips.start, clips.start + clips.length);
                    for (int clipIndex = 0; clipIndex < clipObjects.Count; clipIndex++) {
                        TimelineObjectSpan clip = clipObjects[clipIndex];
                        List<TimelinePropertySpan> clipProperties = ReadTimelineDirectProperties(source, clip.start, clip.start + clip.length);
                        CollectTimelineCurves(source, FindTimelineProperty(clipProperties, "Controllers"), true, animationCurves);
                        CollectTimelineCurves(source, FindTimelineProperty(clipProperties, "FloatParams"), false, animationCurves);
                    }
                    if (animationCurves.Count == 0) continue;
                    for (int c = 0; c < animationCurves.Count; c++) rewrites.Add(animationCurves[c]);
                    TimelineRewriteSpan header = new TimelineRewriteSpan();
                    header.start = animation.start + 1;
                    header.animationHeader = true;
                    rewrites.Add(header);
                    info.animations++;
                    info.curves += animationCurves.Count;
                }
            }
            if (info.animations == 0 || info.curves == 0) { error = "legacy Timeline animation was not found"; return false; }

            rewrites.Sort(delegate(TimelineRewriteSpan a, TimelineRewriteSpan b) {
                int compare = a.start.CompareTo(b.start);
                if (compare != 0) return compare;
                return a.animationHeader == b.animationHeader ? 0 : (a.animationHeader ? -1 : 1);
            });
            string cacheDir = Path.GetDirectoryName(cachePath);
            Directory.CreateDirectory(cacheDir);
            tempPath = cachePath + ".tmp_" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "_" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
            using(StreamWriter writer = new StreamWriter(tempPath, false, new UTF8Encoding(false), 1024 * 1024)) {
                int cursor = 0;
                for (int i = 0; i < rewrites.Count; i++) {
                    TimelineRewriteSpan rewrite = rewrites[i];
                    if (rewrite.start < cursor) throw new InvalidDataException("overlapping Timeline rewrite at " + rewrite.start);
                    WriteTimelineSlice(writer, source, cursor, rewrite.start - cursor);
                    if (rewrite.animationHeader) {
                        writer.Write("\"SerializeVersion\":\"283\",\"SerializeMode\":\"2\",");
                    } else {
                        info.keyframes += WriteTimelineOptimizedCurve(writer, source, rewrite.start, rewrite.start + rewrite.length);
                        cursor = rewrite.start + rewrite.length;
                    }
                    if (rewrite.animationHeader) cursor = rewrite.start;
                }
                WriteTimelineSlice(writer, source, cursor, source.Length - cursor);
            }
            info.outputBytes = new FileInfo(tempPath).Length;
            if (info.outputBytes <= 0) throw new InvalidDataException("optimized Timeline cache is empty");
            if (File.Exists(cachePath)) File.Delete(cachePath);
            File.Move(tempPath, cachePath);
            tempPath = "";
            return true;
        } catch(Exception e) {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        } finally {
            if (!string.IsNullOrEmpty(tempPath)) { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch {} }
        }
    }

    private static void CollectTimelineCurves(string source, TimelinePropertySpan targets, bool controllers, List<TimelineRewriteSpan> result) {
        if (targets == null || targets.kind != '[') return;
        List<TimelineObjectSpan> targetObjects = ReadTimelineObjectArray(source, targets.start, targets.start + targets.length);
        for (int targetIndex = 0; targetIndex < targetObjects.Count; targetIndex++) {
            TimelineObjectSpan target = targetObjects[targetIndex];
            List<TimelinePropertySpan> properties = ReadTimelineDirectProperties(source, target.start, target.start + target.length);
            for (int propertyIndex = 0; propertyIndex < properties.Count; propertyIndex++) {
                TimelinePropertySpan property = properties[propertyIndex];
                bool isCurve = controllers
                    ? property.key == "X" || property.key == "Y" || property.key == "Z" || property.key == "RotX" || property.key == "RotY" || property.key == "RotZ" || property.key == "RotW"
                    : property.key == "Value";
                if (!isCurve || property.kind != '[') continue;
                char first = FirstTimelineArrayValueKind(source, property.start, property.start + property.length);
                if (first == '\0') continue;
                if (first != '{') throw new InvalidDataException("Timeline curve is not in legacy object format at " + property.start);
                TimelineRewriteSpan rewrite = new TimelineRewriteSpan();
                rewrite.start = property.start;
                rewrite.length = property.length;
                result.Add(rewrite);
            }
        }
    }

    private static char FirstTimelineArrayValueKind(string source, int start, int end) {
        int cursor = start + 1;
        while (cursor < end && (char.IsWhiteSpace(source[cursor]) || source[cursor] == ',')) cursor++;
        if (cursor >= end || source[cursor] == ']') return '\0';
        return source[cursor];
    }

    private static TimelinePropertySpan FindTimelineDirectProperty(string source, int start, int end, string key) {
        return FindTimelineProperty(ReadTimelineDirectProperties(source, start, end), key);
    }

    private static TimelinePropertySpan FindTimelineProperty(List<TimelinePropertySpan> properties, string key) {
        for (int i = 0; i < properties.Count; i++) if (string.Equals(properties[i].key, key, StringComparison.Ordinal)) return properties[i];
        return null;
    }

    private static List<TimelinePropertySpan> ReadTimelineDirectProperties(string source, int start, int end) {
        List<TimelinePropertySpan> result = new List<TimelinePropertySpan>();
        if (string.IsNullOrEmpty(source) || start < 0 || start >= end || source[start] != '{') return result;
        int cursor = start + 1;
        while (cursor < end - 1) {
            while (cursor < end && (char.IsWhiteSpace(source[cursor]) || source[cursor] == ',')) cursor++;
            if (cursor >= end - 1 || source[cursor] != '"') break;
            string key;
            int afterKey;
            if (!TryReadJsonString(source, cursor, end, out key, out afterKey)) break;
            cursor = afterKey;
            while (cursor < end && char.IsWhiteSpace(source[cursor])) cursor++;
            if (cursor >= end || source[cursor++] != ':') break;
            while (cursor < end && char.IsWhiteSpace(source[cursor])) cursor++;
            int valueStart = cursor;
            if (cursor >= end) break;
            char kind = source[cursor];
            if (kind == '{' || kind == '[') {
                int close = FindMatchingJsonContainer(source, cursor, kind, kind == '{' ? '}' : ']');
                if (close < 0 || close >= end) break;
                cursor = close + 1;
            } else if (kind == '"') {
                string ignored;
                int next;
                if (!TryReadJsonString(source, cursor, end, out ignored, out next)) break;
                cursor = next;
            } else {
                while (cursor < end && source[cursor] != ',' && source[cursor] != '}') cursor++;
            }
            TimelinePropertySpan property = new TimelinePropertySpan();
            property.key = key;
            property.start = valueStart;
            property.length = cursor - valueStart;
            property.kind = kind;
            result.Add(property);
        }
        return result;
    }

    private static List<TimelineObjectSpan> ReadTimelineObjectArray(string source, int start, int end) {
        List<TimelineObjectSpan> result = new List<TimelineObjectSpan>();
        if (string.IsNullOrEmpty(source) || start < 0 || start >= end || source[start] != '[') return result;
        int cursor = start + 1;
        while (cursor < end - 1) {
            while (cursor < end && (char.IsWhiteSpace(source[cursor]) || source[cursor] == ',')) cursor++;
            if (cursor >= end - 1 || source[cursor] == ']') break;
            if (source[cursor] != '{') throw new InvalidDataException("expected JSON object at " + cursor);
            int close = FindMatchingJsonContainer(source, cursor, '{', '}');
            if (close < 0 || close >= end) throw new InvalidDataException("unclosed JSON object at " + cursor);
            TimelineObjectSpan item = new TimelineObjectSpan();
            item.start = cursor;
            item.length = close - cursor + 1;
            result.Add(item);
            cursor = close + 1;
        }
        return result;
    }

    private static long WriteTimelineOptimizedCurve(TextWriter writer, string source, int start, int end) {
        writer.Write('[');
        long written = 0;
        float lastTime = -1f;
        float lastValue = 0f;
        int lastCurveType = 3;
        bool first = true;
        int cursor = start + 1;
        while (cursor < end - 1) {
            while (cursor < end && (char.IsWhiteSpace(source[cursor]) || source[cursor] == ',')) cursor++;
            if (cursor >= end - 1 || source[cursor] == ']') break;
            if (source[cursor] != '{') throw new InvalidDataException("expected legacy Timeline keyframe at " + cursor);
            int close = FindMatchingJsonContainer(source, cursor, '{', '}');
            if (close < 0 || close >= end) throw new InvalidDataException("unclosed Timeline keyframe at " + cursor);
            List<TimelinePropertySpan> fields = ReadTimelineDirectProperties(source, cursor, close + 1);
            string text;
            if (!TryReadTimelineScalar(source, fields, "t", out text)) throw new InvalidDataException("Timeline keyframe has no time at " + cursor);
            float time = SnapTimelineFloat(float.Parse(text, CultureInfo.InvariantCulture));
            float value = TryReadTimelineScalar(source, fields, "v", out text) ? float.Parse(text, CultureInfo.InvariantCulture) : lastValue;
            int curveType = TryReadTimelineScalar(source, fields, "c", out text) ? int.Parse(text, CultureInfo.InvariantCulture) : lastCurveType;
            if (Math.Abs(time - lastTime) > float.Epsilon) {
                if (!first) writer.Write(',');
                if (curveType == 0) WriteTimelineLeaveAsIsKeyframe(writer, source, fields, time, value, curveType);
                else writer.Write('"' + EncodeTimelineKeyframe(time, value, curveType, lastValue, lastCurveType) + '"');
                first = false;
                written++;
                lastTime = time;
                lastValue = value;
                lastCurveType = curveType;
            }
            cursor = close + 1;
        }
        writer.Write(']');
        return written;
    }

    private static bool TryReadTimelineScalar(string source, List<TimelinePropertySpan> fields, string key, out string value) {
        value = "";
        TimelinePropertySpan field = FindTimelineProperty(fields, key);
        if (field == null) return false;
        if (field.kind == '"') {
            int next;
            return TryReadJsonString(source, field.start, field.start + field.length, out value, out next);
        }
        value = source.Substring(field.start, field.length).Trim();
        return value.Length > 0;
    }

    private static void WriteTimelineLeaveAsIsKeyframe(TextWriter writer, string source, List<TimelinePropertySpan> fields, float time, float value, int curveType) {
        writer.Write("{\"t\":\"");
        writer.Write(time.ToString(CultureInfo.InvariantCulture));
        writer.Write("\",\"v\":\"");
        writer.Write(value.ToString(CultureInfo.InvariantCulture));
        writer.Write("\",\"c\":\"");
        writer.Write(curveType.ToString(CultureInfo.InvariantCulture));
        writer.Write('"');
        string text;
        if (TryReadTimelineScalar(source, fields, "i", out text)) { writer.Write(",\"i\":\""); writer.Write(text); writer.Write('"'); }
        if (TryReadTimelineScalar(source, fields, "o", out text)) { writer.Write(",\"o\":\""); writer.Write(text); writer.Write('"'); }
        writer.Write('}');
    }

    private static float SnapTimelineFloat(float value) {
        value = (float)(Math.Round(value * 1000f) / 1000f);
        return value < 0f ? 0f : value;
    }

    private static string EncodeTimelineKeyframe(float time, float value, int curveType, float lastValue, int lastCurveType) {
        bool hasValue = Math.Abs(lastValue - value) > float.Epsilon;
        bool hasCurveType = lastCurveType != curveType;
        StringBuilder sb = new StringBuilder(19);
        sb.Append((char)('A' + (hasValue ? 1 : 0) + (hasCurveType ? 2 : 0)));
        AppendTimelineFloatHex(sb, time);
        if (hasValue) AppendTimelineFloatHex(sb, value);
        if (hasCurveType) AppendTimelineByteHex(sb, (byte)curveType);
        return sb.ToString();
    }

    private static readonly char[] TimelineHex = "0123456789ABCDEF".ToCharArray();

    private static void AppendTimelineFloatHex(StringBuilder sb, float value) {
        byte[] bytes = BitConverter.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++) AppendTimelineByteHex(sb, bytes[i]);
    }

    private static void AppendTimelineByteHex(StringBuilder sb, byte value) {
        sb.Append(TimelineHex[value >> 4]);
        sb.Append(TimelineHex[value & 15]);
    }

    private static void WriteTimelineSlice(TextWriter writer, string source, int start, int length) {
        const int BufferSize = 1024 * 1024;
        if (length <= 0) return;
        char[] buffer = new char[Math.Min(BufferSize, length)];
        int cursor = start;
        int remaining = length;
        while (remaining > 0) {
            int count = Math.Min(buffer.Length, remaining);
            source.CopyTo(cursor, buffer, 0, count);
            writer.Write(buffer, 0, count);
            cursor += count;
            remaining -= count;
        }
    }

    private int LoadCacheIntoMemory() {
        int n=0;
        try {
            Dictionary<string,PackageLite> map=LoadCacheMap();
            all.Clear(); allExact.Clear(); allLatest.Clear();
            foreach(PackageLite p in map.Values) { all.Add(p); AddMaps(p, allExact, allLatest); n++; }
            all.Sort((a,b)=>string.Compare(a.uid,b.uid,StringComparison.OrdinalIgnoreCase));
            scanned = n > 0;
        } catch(Exception e) { Logger.LogWarning("Load cache failed: "+e.Message); }
        return n;
    }
    private Dictionary<string, PackageLite> LoadCacheMap() {
        Dictionary<string, PackageLite> map = new Dictionary<string, PackageLite>(StringComparer.OrdinalIgnoreCase);
        if(!File.Exists(indexPath)) return map;
        string[] lines=File.ReadAllLines(indexPath,Encoding.UTF8);
        for(int i=0;i<lines.Length;i++) {
            string line=lines[i]; if(line==null || line.Length==0 || line.StartsWith("#")) continue;
            string[] f=line.Split('\t'); if(f.Length<11) continue;
            try {
                PackageLite p=new PackageLite();
                p.fullPath=Dec(f[0]); p.relPath=Dec(f[1]); p.uid=Dec(f[2]); p.size=ParseLong(f[3]); p.mtimeUtcTicks=ParseLong(f[4]); p.description=Dec(f[5]); p.thumbEntry=Dec(f[6]); p.firstScene=Dec(f[7]); p.thumbCache=Dec(f[8]); p.cats=SplitList(Dec(f[9])); p.deps=SplitList(Dec(f[10])); if(f.Length>=12) p.scenes=SplitList(Dec(f[11])); if(f.Length>=13) p.presetSpecs=SplitList(Dec(f[12])); if(p.scenes.Count==0 && p.firstScene!="") p.scenes.Add(p.firstScene);
                if(p.uid!="" && p.fullPath!="") map[p.fullPath]=p;
            } catch {}
        }
        return map;
    }
    private void SaveCache(List<PackageLite> packages) {
        try {
            List<string> lines=new List<string>(); lines.Add(CacheHeader);
            for(int i=0;i<packages.Count;i++) {
                PackageLite p=packages[i];
                lines.Add(Enc(p.fullPath)+"\t"+Enc(p.relPath)+"\t"+Enc(p.uid)+"\t"+p.size+"\t"+p.mtimeUtcTicks+"\t"+Enc(p.description)+"\t"+Enc(p.thumbEntry)+"\t"+Enc(p.firstScene)+"\t"+Enc(p.thumbCache)+"\t"+Enc(JoinList(p.cats))+"\t"+Enc(JoinList(p.deps))+"\t"+Enc(JoinList(p.scenes))+"\t"+Enc(JoinList(p.presetSpecs)));
            }
            string tmp=indexPath+".tmp"; File.WriteAllLines(tmp,lines.ToArray(),Encoding.UTF8); if(File.Exists(indexPath)) File.Delete(indexPath); File.Move(tmp,indexPath);
        } catch(Exception e) { Logger.LogWarning("Save cache failed: "+e.Message); }
    }
    private string Enc(string s) { if(s==null) s=""; return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
    private string Dec(string s) { try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); } catch { return ""; } }
    private long ParseLong(string s) { long v; return long.TryParse(s,out v)?v:0; }
    private string JoinList(List<string> l) { return l==null?"":string.Join(ListSep,l.ToArray()); }
    private List<string> SplitList(string s) { List<string> l=new List<string>(); if(string.IsNullOrEmpty(s)) return l; string[] a=s.Split(new string[]{ListSep},StringSplitOptions.RemoveEmptyEntries); for(int i=0;i<a.Length;i++) l.Add(a[i]); return l; }

    private string MakeRel(string root, string file) { Uri u=new Uri(AppendSlash(Path.GetFullPath(root))); Uri f=new Uri(Path.GetFullPath(file)); return Uri.UnescapeDataString(u.MakeRelativeUri(f).ToString()).Replace('/',Path.DirectorySeparatorChar); }
    private string AppendSlash(string p) { return p.EndsWith("\\")||p.EndsWith("/") ? p : p+Path.DirectorySeparatorChar; }
    private void AddMaps(PackageLite p, Dictionary<string,PackageLite> exact, Dictionary<string,PackageLite> latest) { if(!exact.ContainsKey(p.uid)) exact[p.uid]=p; string k=Group(p.uid)+".latest"; PackageLite old; if(!latest.TryGetValue(k,out old)||Version(p.uid)>Version(old.uid)) latest[k]=p; }
    private string Group(string uid) { int i=uid.LastIndexOf('.'); int dummy; if(i>0 && int.TryParse(uid.Substring(i+1), out dummy)) return uid.Substring(0,i); return uid; }
    private int Version(string uid) { int i=uid.LastIndexOf('.'); int v; return i>0 && int.TryParse(uid.Substring(i+1),out v)?v:0; }

        private void BuildPanel() {
        DebugLog("BuildPanel begin.");
        if (canvas != null) ClosePanel();
        // "其他" 已并入“全部”二级筛选，兼容旧会话状态
        if (activeCat == "Other") { activeCat = "All"; allSubFilter = "Other"; }

        isVRMode = openedViaVR && CanPlaceVrCanvas();
        if (isVRMode && pageSize > 64) { pageSizeBeforeVr = pageSize; pageSize = 64; DebugLog("VR pageSize capped to 64 for frame-rate stability; desktop value will be restored on close."); }
        if (openedViaVR && !isVRMode) DebugLog("BuildPanel VR requested but view is not ready; using overlay fallback.");
        DebugLog("BuildPanel VR detection: isVRMode=" + isVRMode + ", openedViaVR=" + openedViaVR);

        root = new GameObject("AllPackagesLinkerCanvas");
        canvas = root.AddComponent<Canvas>();

        if (isVRMode) {
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = 30;
            try { if (Camera.main != null) canvas.worldCamera = Camera.main; } catch {}
            RectTransform rootRt = root.GetComponent<RectTransform>();
            rootRt.sizeDelta = new Vector2(1920, 1080);
            root.AddComponent<GraphicRaycaster>();
            if (SuperController.singleton != null) {
                try { SuperController.singleton.AddCanvas(canvas); } catch (Exception e) { DebugLog("AddCanvas failed: " + e.ToString()); }
            }
            ApplyCanvasTransform();
        } else {
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 200;
            CanvasScaler scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            root.AddComponent<GraphicRaycaster>();
            if (SuperController.singleton != null) {
                try { SuperController.singleton.AddCanvas(canvas); } catch (Exception e) { DebugLog("AddCanvas failed: " + e.ToString()); }
            }
        }

        Image bgImg = root.AddComponent<Image>();
        bgImg.color = colBg;

        float appBarH = isVRMode ? 64f : 54f;
        float statusH = isVRMode ? 58f : 46f;
        float navW = isVRMode ? 0.13f : 0.11f;
        float inspectorL = isVRMode ? 0.68f : 0.72f;

        // === APP BAR ===
        GameObject topBar = new GameObject("AppBar");
        topBar.transform.SetParent(root.transform, false);
        Image topBarBg = topBar.AddComponent<Image>();
        topBarBg.color = colPanel;
        RectTransform topBarRt = topBar.GetComponent<RectTransform>();
        topBarRt.anchorMin = new Vector2(0, 1); topBarRt.anchorMax = new Vector2(1, 1);
        topBarRt.pivot = new Vector2(0.5f, 1);
        topBarRt.offsetMin = new Vector2(0, -appBarH); topBarRt.offsetMax = new Vector2(0, 0);

        // 顶栏三段式：标题 | 搜索 | 右侧重按钮（固定宽度+间距，避免挤成一团）
        header = MakeText(topBar.transform, "Header", "AllPackagesLinker", isVRMode ? 17 : 16, TextAnchor.MiddleLeft, colTextPrimary);
        RectTransform hrt = header.rectTransform;
        hrt.anchorMin = new Vector2(0, 0); hrt.anchorMax = new Vector2(0, 1);
        hrt.pivot = new Vector2(0, 0.5f);
        hrt.anchoredPosition = new Vector2(14, 0);
        hrt.sizeDelta = new Vector2(isVRMode ? 200f : 180f, 0);

        float rightClusterW = isVRMode ? 280f : 240f; // 设置 + 间距 + 关闭
        float titleW = isVRMode ? 210f : 190f;
        float sidePad = 12f;
        float topBtnGap = 12f;

        GameObject searchObj = new GameObject("SearchBar");
        searchObj.transform.SetParent(topBar.transform, false);
        Image searchBg = searchObj.AddComponent<Image>();
        searchBg.color = new Color(0.12f, 0.14f, 0.18f, 0.98f);
        RectTransform searchRt = searchObj.GetComponent<RectTransform>();
        searchRt.anchorMin = new Vector2(0, 0.14f); searchRt.anchorMax = new Vector2(1, 0.86f);
        searchRt.pivot = new Vector2(0.5f, 0.5f);
        searchRt.offsetMin = new Vector2(titleW, 0);
        searchRt.offsetMax = new Vector2(-(rightClusterW + sidePad + 8f), 0);
        searchInput = searchObj.AddComponent<InputField>();
        Text searchTxt = MakeText(searchObj.transform, "Text", "", 15, TextAnchor.MiddleLeft, colTextPrimary);
        RectTransform stxtRt = searchTxt.rectTransform;
        stxtRt.anchorMin = Vector2.zero; stxtRt.anchorMax = Vector2.one;
        stxtRt.offsetMin = new Vector2(12, 0); stxtRt.offsetMax = new Vector2(-44, 0);
        searchPlaceholderText = MakeText(searchObj.transform, "Placeholder", "搜索场景名、包名或作者...", 15, TextAnchor.MiddleLeft, colTextDim);
        RectTransform sphRt = searchPlaceholderText.rectTransform;
        sphRt.anchorMin = Vector2.zero; sphRt.anchorMax = Vector2.one;
        sphRt.offsetMin = new Vector2(12, 0); sphRt.offsetMax = new Vector2(-44, 0);
        searchInput.textComponent = searchTxt;
        searchInput.placeholder = searchPlaceholderText;
        if (!string.IsNullOrEmpty(searchQuery)) searchInput.text = searchQuery;
        searchInput.onValueChanged.AddListener((string val) => { searchQuery = val; page = 0; SaveCurrentPageState(); RefreshList(); });

        searchClearBtn = MakeButton(searchObj.transform, "×", 16, colBtn);
        RectTransform clearRt = searchClearBtn.GetComponent<RectTransform>();
        clearRt.anchorMin = new Vector2(1, 0.12f); clearRt.anchorMax = new Vector2(1, 0.88f);
        clearRt.pivot = new Vector2(1, 0.5f);
        clearRt.anchoredPosition = new Vector2(-6, 0);
        clearRt.sizeDelta = new Vector2(34, 0);
        searchClearBtn.onClick.AddListener(() => {
            searchQuery = "";
            if (searchInput != null) searchInput.text = "";
            page = 0; SaveCurrentPageState(); RefreshList();
        });

        // 右侧按钮容器：水平排布，明确间距
        GameObject rightBar = new GameObject("RightActions");
        rightBar.transform.SetParent(topBar.transform, false);
        RectTransform rightRt = rightBar.AddComponent<RectTransform>();
        rightRt.anchorMin = new Vector2(1, 0); rightRt.anchorMax = new Vector2(1, 1);
        rightRt.pivot = new Vector2(1, 0.5f);
        rightRt.anchoredPosition = new Vector2(-sidePad, 0);
        rightRt.sizeDelta = new Vector2(rightClusterW, 0);
        HorizontalLayoutGroup rightHlg = rightBar.AddComponent<HorizontalLayoutGroup>();
        rightHlg.spacing = topBtnGap;
        rightHlg.childAlignment = TextAnchor.MiddleRight;
        rightHlg.childForceExpandWidth = false;
        rightHlg.childForceExpandHeight = true;
        rightHlg.childControlWidth = true;
        rightHlg.childControlHeight = true;
        rightHlg.padding = new RectOffset(0, 0, 6, 6);

        float topBtnW = isVRMode ? 120f : 100f;
        settingsBtn = MakeButton(rightBar.transform, "设置", isVRMode ? 16 : 15, colAccentDim);
        SetFlexibleItem(settingsBtn.gameObject, topBtnW, 0f);
        LayoutElement setLe = settingsBtn.gameObject.GetComponent<LayoutElement>();
        if (setLe != null) { setLe.preferredWidth = topBtnW; setLe.minWidth = topBtnW; }
        settingsBtn.onClick.AddListener(() => ToggleSettingsDrawer());

        // 重新扫描移入设置抽屉
        rescanTopBtn = null;

        Button closeTop = MakeButton(rightBar.transform, "关闭", isVRMode ? 16 : 15, colDanger);
        SetFlexibleItem(closeTop.gameObject, topBtnW, 0f);
        LayoutElement closeLe = closeTop.gameObject.GetComponent<LayoutElement>();
        if (closeLe != null) { closeLe.preferredWidth = topBtnW; closeLe.minWidth = topBtnW; }
        closeTop.onClick.AddListener(() => ClosePanel());

        // === LEFT NAV ===
        navRoot = new GameObject("Nav");
        navRoot.transform.SetParent(root.transform, false);
        Image navBg = navRoot.AddComponent<Image>();
        navBg.color = colPanel;
        RectTransform navRt = navRoot.GetComponent<RectTransform>();
        navRt.anchorMin = new Vector2(0, statusH / 1080f); navRt.anchorMax = new Vector2(navW, 1f - appBarH / 1080f);
        navRt.offsetMin = new Vector2(6, 6); navRt.offsetMax = new Vector2(-4, -6);
        VerticalLayoutGroup navVlg = navRoot.AddComponent<VerticalLayoutGroup>();
        navVlg.spacing = 4; navVlg.padding = new RectOffset(6, 6, 8, 8);
        navVlg.childForceExpandWidth = true; navVlg.childForceExpandHeight = false;
        navVlg.childControlWidth = true; navVlg.childControlHeight = false;
        navVlg.childAlignment = TextAnchor.UpperCenter;

        tabCats = new string[]{"Favorites","Scenes","Presets","Clothing","Hair","Morphs","Scripts","All"};
        tabBgs.Clear();
        for (int i = 0; i < tabCats.Length; i++) {
            string cat = tabCats[i];
            GameObject tabObj = new GameObject("Nav_" + cat);
            tabObj.transform.SetParent(navRoot.transform, false);
            Image tabBg = tabObj.AddComponent<Image>();
            tabBg.color = (cat == activeCat) ? colAccentDim : new Color(0, 0, 0, 0.02f);
            tabBgs.Add(tabBg);
            Button tabBtn = tabObj.AddComponent<Button>();
            tabBtn.targetGraphic = tabBg;
            LayoutElement tabLe = tabObj.AddComponent<LayoutElement>();
            tabLe.preferredHeight = isVRMode ? 52f : 42f;
            tabLe.minHeight = isVRMode ? 52f : 42f;
            Text tabText = MakeText(tabObj.transform, "Label", CatLabel(cat), isVRMode ? 16 : 15, TextAnchor.MiddleLeft, (cat == activeCat) ? colTextPrimary : colTextSecondary);
            RectTransform trt = tabText.rectTransform;
            trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
            trt.offsetMin = new Vector2(12, 0); trt.offsetMax = new Vector2(-6, 0);
            // accent side bar
            GameObject bar = new GameObject("Accent");
            bar.transform.SetParent(tabObj.transform, false);
            Image barImg = bar.AddComponent<Image>();
            barImg.color = (cat == activeCat) ? colAccent : new Color(0, 0, 0, 0);
            barImg.raycastTarget = false;
            RectTransform barRt = bar.GetComponent<RectTransform>();
            barRt.anchorMin = new Vector2(0, 0.15f); barRt.anchorMax = new Vector2(0, 0.85f);
            barRt.pivot = new Vector2(0, 0.5f); barRt.anchoredPosition = Vector2.zero; barRt.sizeDelta = new Vector2(4, 0);
            tabBtn.onClick.AddListener(() => {
                activeCat = cat;
                if (cat != "All") allSubFilter = "All";
                RestorePageForCurrentTab();
                UpdateTabHighlights();
                ClearSelectionKeepPreview(false);
                RefreshList();
            });
        }

        // === RESULT TOOLBAR + SUB FILTERS ===
        subBarRoot = new GameObject("ResultToolbar");
        subBarRoot.transform.SetParent(root.transform, false);
        Image subBarBg = subBarRoot.AddComponent<Image>();
        subBarBg.color = new Color(0.16f, 0.18f, 0.23f, 0.96f);
        RectTransform subBarRt = subBarRoot.GetComponent<RectTransform>();
        subBarRt.anchorMin = new Vector2(navW, 1f - (appBarH + 40f) / 1080f);
        subBarRt.anchorMax = new Vector2(inspectorL, 1f - appBarH / 1080f);
        subBarRt.offsetMin = new Vector2(4, 0); subBarRt.offsetMax = new Vector2(-6, -4);
        HorizontalLayoutGroup subHlg = subBarRoot.AddComponent<HorizontalLayoutGroup>();
        subHlg.spacing = 8; subHlg.childForceExpandWidth = false; subHlg.childForceExpandHeight = true;
        subHlg.padding = new RectOffset(10, 10, 4, 4); subHlg.childAlignment = TextAnchor.MiddleLeft;
        BuildSubBarButtons();

        // === LIST ===
        MakeList(navW, inspectorL, appBarH + 40f, statusH + 4f);

        // === INSPECTOR ===
        GameObject detailPanel = new GameObject("Inspector");
        detailPanel.transform.SetParent(root.transform, false);
        Image detailBg = detailPanel.AddComponent<Image>();
        detailBg.color = colPanel;
        RectTransform dpRt = detailPanel.GetComponent<RectTransform>();
        dpRt.anchorMin = new Vector2(inspectorL, statusH / 1080f);
        dpRt.anchorMax = new Vector2(0.995f, 1f - appBarH / 1080f);
        dpRt.offsetMin = new Vector2(4, 6); dpRt.offsetMax = new Vector2(-6, -6);

        ScrollRect detailScroll = detailPanel.AddComponent<ScrollRect>();
        detailScroll.horizontal = false;
        detailScroll.scrollSensitivity = 24f;

        GameObject detailViewport = new GameObject("DetailViewport");
        detailViewport.transform.SetParent(detailPanel.transform, false);
        RectTransform dvRt = detailViewport.AddComponent<RectTransform>();
        dvRt.anchorMin = Vector2.zero; dvRt.anchorMax = Vector2.one;
        dvRt.offsetMin = new Vector2(8, 8); dvRt.offsetMax = new Vector2(-8, -8);
        Image dvImg = detailViewport.AddComponent<Image>();
        dvImg.color = new Color(0, 0, 0, 0.01f);
        detailViewport.AddComponent<Mask>().showMaskGraphic = false;

        GameObject detailContent = new GameObject("DetailContent");
        detailContent.transform.SetParent(detailViewport.transform, false);
        RectTransform dcRt = detailContent.AddComponent<RectTransform>();
        dcRt.anchorMin = new Vector2(0, 1); dcRt.anchorMax = new Vector2(1, 1);
        dcRt.pivot = new Vector2(0.5f, 1);
        dcRt.offsetMin = Vector2.zero; dcRt.offsetMax = Vector2.zero;
        VerticalLayoutGroup detailVlg = detailContent.AddComponent<VerticalLayoutGroup>();
        detailVlg.spacing = 12; detailVlg.padding = new RectOffset(4, 4, 4, 12);
        detailVlg.childAlignment = TextAnchor.UpperCenter;
        detailVlg.childControlWidth = true; detailVlg.childControlHeight = false;
        detailVlg.childForceExpandWidth = true; detailVlg.childForceExpandHeight = false;
        ContentSizeFitter detailFit = detailContent.AddComponent<ContentSizeFitter>();
        detailFit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        detailScroll.viewport = dvRt;
        detailScroll.content = dcRt;

        // 预览略缩小，给详情和按钮留空间
        preview = MakeImage(CreateSection(detailContent.transform, "PreviewSection", isVRMode ? 200f : 180f).transform, "Preview", colThumbBg);
        preview.preserveAspect = true;
        StretchFull(preview.rectTransform, 0, 0, 0, 0);

        // 详情区：固定高度 + 裁剪，禁止文字溢出叠到下方按钮
        float detailsH = isVRMode ? 168f : 150f;
        GameObject detailsSection = CreateSection(detailContent.transform, "DetailsSection", detailsH);
        Image detailsBg = detailsSection.GetComponent<Image>();
        if (detailsBg != null) {
            detailsBg.color = new Color(0.14f, 0.16f, 0.20f, 1f);
            detailsBg.raycastTarget = false;
        }
        // Unity UI Mask 裁剪子文字，避免“挤进”下方按钮
        detailsSection.AddComponent<RectMask2D>();
        details = MakeText(detailsSection.transform, "Details", "请从左侧选择一个资源后执行操作。", 14, TextAnchor.UpperLeft, colTextDim);
        details.horizontalOverflow = HorizontalWrapMode.Wrap;
        details.verticalOverflow = VerticalWrapMode.Truncate;
        details.raycastTarget = false;
        StretchFull(details.rectTransform, 10, 10, 8, 8);

        atomRowRoot = CreateRow(detailContent.transform, "AtomRow", 40f, 8, true);
        Button atomBtn = MakeButton(atomRowRoot.transform, "切换目标", 14, colBtn);
        SetFlexibleItem(atomBtn.gameObject, 90f, 0);
        atomBtn.onClick.AddListener(() => CycleTargetAtom());
        atomSelectorLabel = MakeText(atomRowRoot.transform, "AtomLabel", "点击选择原子", 13, TextAnchor.MiddleLeft, colTextPrimary);
        LayoutElement atomLblLe = atomSelectorLabel.gameObject.AddComponent<LayoutElement>();
        atomLblLe.flexibleWidth = 1f; atomLblLe.minHeight = 40f;
        RefreshAtomDropdown();

        presetOptionsRoot = CreateRow(detailContent.transform, "PresetOptionsRow", 34f, 8, true);
        applyClothingToggle = MakeToggle(presetOptionsRoot.transform, "包含服装", applyClothing);
        SetFlexibleItem(applyClothingToggle.gameObject, 0f, 1f);
        applyClothingToggle.onValueChanged.AddListener((bool v) => { applyClothing = v; SetStatus("预设加载：" + (v ? "包含服装" : "仅模型服装不变"), false); });
        applyHairToggle = MakeToggle(presetOptionsRoot.transform, "包含头发", applyHair);
        SetFlexibleItem(applyHairToggle.gameObject, 0f, 1f);
        applyHairToggle.onValueChanged.AddListener((bool v) => { applyHair = v; SetStatus("预设加载：" + (v ? "包含头发" : "仅模型头发不变"), false); });

        // 快捷模式按钮已移除：只保留“包含服装/头发”开关，界面更干净
        presetModeRoot = null;

        sceneModeRoot = CreateRow(detailContent.transform, "SceneLoadModeRow", isVRMode ? 46f : 40f, 6, true);
        sceneFullModeBtn = MakeButton(sceneModeRoot.transform, "完整", 13, colBtn);
        SetFlexibleItem(sceneFullModeBtn.gameObject, 0f, 1f);
        sceneFullModeBtn.onClick.AddListener(() => SetSceneLoadMode(0));
        scenePrimaryModeBtn = MakeButton(sceneModeRoot.transform, "人物优先", 13, colBtn);
        SetFlexibleItem(scenePrimaryModeBtn.gameObject, 0f, 1f);
        scenePrimaryModeBtn.onClick.AddListener(() => SetSceneLoadMode(1));
        sceneMinimalModeBtn = MakeButton(sceneModeRoot.transform, "极简人物", 13, colBtn);
        SetFlexibleItem(sceneMinimalModeBtn.gameObject, 0f, 1f);
        sceneMinimalModeBtn.onClick.AddListener(() => SetSceneLoadMode(2));

        scenePersonRoot = CreateRow(detailContent.transform, "ScenePrimaryPersonRow", isVRMode ? 46f : 40f, 8, true);
        Button scenePersonBtn = MakeButton(scenePersonRoot.transform, "切换主角", 13, colBtn);
        SetFlexibleItem(scenePersonBtn.gameObject, 96f, 0f);
        scenePersonBtn.onClick.AddListener(() => CycleScenePrimaryPerson());
        scenePrimaryPersonLabel = MakeText(scenePersonRoot.transform, "ScenePrimaryPersonLabel", "主角：自动", 13, TextAnchor.MiddleLeft, colTextPrimary);
        LayoutElement scenePersonLe = scenePrimaryPersonLabel.gameObject.AddComponent<LayoutElement>();
        scenePersonLe.flexibleWidth = 1f; scenePersonLe.minHeight = isVRMode ? 46f : 40f;

        // Primary actions - only one relevant will show
        sceneActionRoot = CreateRow(detailContent.transform, "SceneActionRow", isVRMode ? 50f : 46f, 8, true);
        loadSceneBtn = MakeButton(sceneActionRoot.transform, "加载场景", isVRMode ? 17 : 16, colAccent);
        SetFlexibleItem(loadSceneBtn.gameObject, 0f, 1f);
        loadSceneBtn.onClick.AddListener(() => {
            if (selectedSceneItem != null) LoadPackageScene(selectedSceneItem.package, selectedSceneItem.entryPath);
            else LinkSelected(true);
        });
        loadDeferredSceneBtn = MakeButton(sceneActionRoot.transform, "加载其余 Atom", isVRMode ? 15 : 14, colSuccess);
        SetFlexibleItem(loadDeferredSceneBtn.gameObject, 0f, 1f);
        loadDeferredSceneBtn.onClick.AddListener(() => LoadDeferredSceneAtoms());
        loadDeferredSceneBtn.gameObject.SetActive(false);

        presetActionRoot = CreateRow(detailContent.transform, "PresetActionRow", 46f, 8, true);
        applyPresetBtn = MakeButton(presetActionRoot.transform, "应用到人物", isVRMode ? 17 : 16, colSuccess);
        SetFlexibleItem(applyPresetBtn.gameObject, 0f, 1f);
        applyPresetBtn.onClick.AddListener(() => ApplySelectedPresetToAtom());

        moreActionsRoot = CreateRow(detailContent.transform, "ScriptActionRow", 46f, 8, true);
        loadScriptBtn = MakeButton(moreActionsRoot.transform, "加载到原子", isVRMode ? 17 : 16, new Color(0.48f, 0.38f, 0.78f, 0.96f));
        SetFlexibleItem(loadScriptBtn.gameObject, 0f, 1f);
        loadScriptBtn.onClick.AddListener(() => LoadScriptToAtom());

        // 次要操作：更高按钮 + 更大间距，不与详情文字叠在一起
        float actionH = isVRMode ? 52f : 48f;
        int actionGap = isVRMode ? 10 : 8;
        linkActionRoot = CreateRow(detailContent.transform, "LinkActionRow", actionH, actionGap, true);
        linkOnlyBtn = MakeButton(linkActionRoot.transform, "仅链接", 15, colBtn);
        SetFlexibleItem(linkOnlyBtn.gameObject, 0f, 1f);
        linkOnlyBtn.onClick.AddListener(() => LinkSelected(false));
        defaultKeepBtn = MakeButton(linkActionRoot.transform, "默认保留", 15, colBtn);
        SetFlexibleItem(defaultKeepBtn.gameObject, 0f, 1f);
        defaultKeepBtn.onClick.AddListener(() => ToggleDefaultSelected());
        favToggleBtn = MakeButton(linkActionRoot.transform, "★ 收藏", 15, colBtn);
        SetFlexibleItem(favToggleBtn.gameObject, 0f, 1f);
        favToggleBtn.onClick.AddListener(() => ToggleFavoriteSelected());

        hubRowRoot = CreateRow(detailContent.transform, "HubRow", actionH, actionGap, true);
        Button hubDetailBtn = MakeButton(hubRowRoot.transform, "Hub", 15, colAccentDim);
        SetFlexibleItem(hubDetailBtn.gameObject, 0f, 1f);
        hubDetailBtn.onClick.AddListener(() => OpenSelectedInHub());
        Button hubDepsBtn = MakeButton(hubRowRoot.transform, "检查依赖", 15, colBtn);
        SetFlexibleItem(hubDepsBtn.gameObject, 0f, 1f);
        hubDepsBtn.onClick.AddListener(() => CheckSelectedMissingDeps());

        hubDownloadRoot = CreateRow(detailContent.transform, "HubDownloadRow", actionH, actionGap, true);
        hubDownloadButton = MakeButton(hubDownloadRoot.transform, missingDepsDownloadRunning ? "取消下载" : "下载缺失依赖", 15, colSuccess);
        SetFlexibleItem(hubDownloadButton.gameObject, 0f, 1f);
        hubDownloadButton.onClick.AddListener(() => DownloadSelectedMissingDepsToLibrary());

        progressSectionRoot = CreateSection(detailContent.transform, "DownloadProgress", 52f);
        progressSectionRoot.GetComponent<Image>().color = new Color(0.12f, 0.16f, 0.20f, 0.90f);
        GameObject progressFillObj = new GameObject("Fill");
        progressFillObj.transform.SetParent(progressSectionRoot.transform, false);
        downloadProgressFill = progressFillObj.AddComponent<Image>();
        downloadProgressFill.color = new Color(0.20f, 0.62f, 0.50f, 0.90f);
        downloadProgressFill.raycastTarget = false;
        RectTransform progressFillRt = downloadProgressFill.rectTransform;
        progressFillRt.anchorMin = Vector2.zero; progressFillRt.anchorMax = new Vector2(Mathf.Max(0.001f, downloadProgressValue), 1f);
        progressFillRt.offsetMin = Vector2.zero; progressFillRt.offsetMax = Vector2.zero;
        downloadProgressText = MakeText(progressSectionRoot.transform, "ProgressText", downloadProgressLabel, 13, TextAnchor.MiddleCenter, colTextPrimary);
        downloadProgressText.raycastTarget = false;
        StretchFull(downloadProgressText.rectTransform, 8, 8, 4, 4);

        Text hint = MakeText(detailContent.transform, "Hint", "F8/F7 打开 · VR:左摇杆按下=镜头模式（左右转/前后升降）", 12, TextAnchor.MiddleCenter, colTextDim);
        hint.horizontalOverflow = HorizontalWrapMode.Wrap;
        hint.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(hint.gameObject, 28f);

        // === STATUS / PAGINATION BAR ===
        GameObject bottomBar = new GameObject("StatusBar");
        bottomBar.transform.SetParent(root.transform, false);
        Image bottomBg = bottomBar.AddComponent<Image>();
        bottomBg.color = colPanel;
        RectTransform bbRt = bottomBar.GetComponent<RectTransform>();
        bbRt.anchorMin = new Vector2(0, 0); bbRt.anchorMax = new Vector2(1, statusH / 1080f);
        bbRt.offsetMin = Vector2.zero; bbRt.offsetMax = Vector2.zero;

        statusText = MakeText(bottomBar.transform, "Status", status, 13, TextAnchor.MiddleLeft, colTextDim);
        RectTransform stRt = statusText.rectTransform;
        stRt.anchorMin = new Vector2(0.01f, 0); stRt.anchorMax = new Vector2(0.42f, 1);
        stRt.offsetMin = new Vector2(8, 0); stRt.offsetMax = Vector2.zero;

        Button prev = MakeButton(bottomBar.transform, "上一页", 14, colBtn);
        RectTransform prevBtnRt = prev.GetComponent<RectTransform>();
        prevBtnRt.anchorMin = new Vector2(0.43f, 0.15f); prevBtnRt.anchorMax = new Vector2(0.50f, 0.85f);
        prevBtnRt.offsetMin = Vector2.zero; prevBtnRt.offsetMax = Vector2.zero;
        prev.onClick.AddListener(() => ChangePage(-1));

        pageStripRoot = new GameObject("PageStrip");
        pageStripRoot.transform.SetParent(bottomBar.transform, false);
        RectTransform psRt = pageStripRoot.AddComponent<RectTransform>();
        psRt.anchorMin = new Vector2(0.505f, 0.10f); psRt.anchorMax = new Vector2(0.78f, 0.90f);
        psRt.offsetMin = Vector2.zero; psRt.offsetMax = Vector2.zero;
        HorizontalLayoutGroup psLayout = pageStripRoot.AddComponent<HorizontalLayoutGroup>();
        psLayout.spacing = 4; psLayout.padding = new RectOffset(2, 2, 0, 0);
        psLayout.childForceExpandWidth = false; psLayout.childForceExpandHeight = true;
        psLayout.childAlignment = TextAnchor.MiddleCenter;

        Button next = MakeButton(bottomBar.transform, "下一页", 14, colBtn);
        RectTransform nextBtnRt = next.GetComponent<RectTransform>();
        nextBtnRt.anchorMin = new Vector2(0.785f, 0.15f); nextBtnRt.anchorMax = new Vector2(0.855f, 0.85f);
        nextBtnRt.offsetMin = Vector2.zero; nextBtnRt.offsetMax = Vector2.zero;
        next.onClick.AddListener(() => ChangePage(1));

        pageInfoText = MakeText(bottomBar.transform, "PageInfo", "第 1/1 页", 12, TextAnchor.MiddleRight, colTextSecondary);
        RectTransform piRt = pageInfoText.rectTransform;
        piRt.anchorMin = new Vector2(0.86f, 0); piRt.anchorMax = new Vector2(0.995f, 1);
        piRt.offsetMin = Vector2.zero; piRt.offsetMax = new Vector2(-8, 0);

        BuildSettingsDrawer();
        UpdateSearchPlaceholder();
        UpdateInspectorVisibility();
        RefreshPageStrip();

        DebugLog("BuildPanel end. childCount=" + root.transform.childCount + ", pageSize=" + pageSize + ", isVR=" + isVRMode);
    }

    private void MakeList(float navW, float inspectorL, float topPx, float bottomPx){
        GameObject so = new GameObject("GridScroll");
        so.transform.SetParent(root.transform, false);
        RectTransform srt = so.AddComponent<RectTransform>();
        srt.anchorMin = new Vector2(navW, bottomPx / 1080f);
        srt.anchorMax = new Vector2(inspectorL, 1f - topPx / 1080f);
        srt.offsetMin = new Vector2(4, 4);
        srt.offsetMax = new Vector2(-6, -4);
        so.AddComponent<Image>().color = colScrollBg;
        ScrollRect sr = so.AddComponent<ScrollRect>();
        sr.horizontal = false;
        sr.scrollSensitivity = 40f;

        GameObject vo = new GameObject("Viewport");
        vo.transform.SetParent(so.transform, false);
        RectTransform vrt = vo.AddComponent<RectTransform>();
        vrt.anchorMin = Vector2.zero; vrt.anchorMax = Vector2.one;
        vrt.offsetMin = new Vector2(12, 8); vrt.offsetMax = new Vector2(-8, -8);
        vo.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
        vo.AddComponent<Mask>().showMaskGraphic = false;

        GameObject co = new GameObject("Content");
        co.transform.SetParent(vo.transform, false);
        RectTransform crt = co.AddComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1);
        crt.pivot = new Vector2(0, 1); crt.anchoredPosition = Vector2.zero;
        crt.sizeDelta = new Vector2(0, 0);

        GridLayoutGroup grid = co.AddComponent<GridLayoutGroup>();
        float cellW = isVRMode ? 188f : 168f;
        float cellH = isVRMode ? 220f : 188f;
        grid.cellSize = new Vector2(cellW, cellH);
        grid.spacing = new Vector2(10, 12);
        grid.padding = new RectOffset(4, 4, 10, 10);
        grid.constraint = GridLayoutGroup.Constraint.Flexible;
        grid.childAlignment = TextAnchor.UpperLeft;

        co.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        sr.viewport = vrt; sr.content = crt; listContent = co.transform;

        emptyStateRoot = new GameObject("EmptyState");
        emptyStateRoot.transform.SetParent(so.transform, false);
        RectTransform esRt = emptyStateRoot.AddComponent<RectTransform>();
        esRt.anchorMin = Vector2.zero; esRt.anchorMax = Vector2.one;
        esRt.offsetMin = Vector2.zero; esRt.offsetMax = Vector2.zero;
        Text emptyTxt = MakeText(emptyStateRoot.transform, "EmptyText", "没有内容", 18, TextAnchor.MiddleCenter, colTextDim);
        StretchFull(emptyTxt.rectTransform, 20, 20, 20, 20);
        emptyStateRoot.SetActive(false);
    }

    // Compatibility shim if any call remains with zero args
    private void MakeList(){ MakeList(0.105f, 0.70f, 96f, 52f); }



    private string PackageAuthor(PackageLite p) {
        string uid = p == null ? "" : (p.uid ?? "").Trim();
        int dot = uid.IndexOf('.');
        return dot > 0 ? uid.Substring(0, dot) : "(未知作者)";
    }
    private bool HasAuthorFilter() { return !string.IsNullOrEmpty(authorFilter) && !string.Equals(authorFilter, "All", StringComparison.OrdinalIgnoreCase); }
    private bool MatchesAuthor(PackageLite p) { return !HasAuthorFilter() || string.Equals(PackageAuthor(p), authorFilter, StringComparison.OrdinalIgnoreCase); }

    private List<PackageLite> Filtered(){ var l=new List<PackageLite>(); string q=searchQuery==null?"":searchQuery.Trim().ToLowerInvariant(); if(activeCat=="Favorites" && favSubCat=="Presets") return l; foreach(var p in all){ if(activeCat=="Favorites") { if(!favoriteUids.Contains(p.uid) && !(p.firstScene!="" && favoriteScenes.Contains(SceneRef(p,p.firstScene))) && !HasFavoriteSceneForPackage(p)) continue; if(favSubCat=="Scenes" && p.firstScene=="") continue; if(favSubCat=="Looks" && !p.cats.Contains("Looks")) continue; if(favSubCat=="Scripts" && !p.cats.Contains("Scripts") && !p.cats.Contains("Plugins")) continue; } else if(activeCat=="Presets") { if(!p.cats.Contains("Presets") && !p.cats.Contains("Looks")) continue; } else if(activeCat=="All") { if(allSubFilter=="Other" && !(p.cats.Count==0 || p.cats.Contains("Other"))) continue; } else if(activeCat!="All" && !p.cats.Contains(activeCat)) continue; if(!MatchesAuthor(p)) continue; if(q!="" && p.uid.ToLowerInvariant().IndexOf(q)<0 && (p.description==null || p.description.ToLowerInvariant().IndexOf(q)<0)) continue; l.Add(p); } return l; }
    private List<PresetLite> FilteredPresets(){ var l=new List<PresetLite>(); if(HasAuthorFilter()) return l; string q=searchQuery==null?"":searchQuery.Trim().ToLowerInvariant(); for(int i=0;i<localPresets.Count;i++){ PresetLite pr=localPresets[i]; if(activeCat=="Favorites" && !favoritePresets.Contains(pr.fullPath)) continue; else if(activeCat!="Favorites" && !PresetTypeMatchesActiveCat(pr.presetType)) continue; if(q!="" && pr.name.ToLowerInvariant().IndexOf(q)<0 && pr.relPath.ToLowerInvariant().IndexOf(q)<0) continue; l.Add(pr); } return l; }
    private void EnsureWearableIndex(){ if(wearableIndexBuilt)return; wearableIndexBuilt=true; wearableItems.Clear(); int errors=0; for(int i=0;i<all.Count;i++){ PackageLite p=all[i]; if(p==null || (!p.cats.Contains("Clothing") && !p.cats.Contains("Hair")))continue; ZipFile z=null; try{ z=new ZipFile(p.fullPath); Dictionary<string,string> previews=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase); List<string> defs=new List<string>(); IEnumerator en=z.GetEnumerator(); while(en.MoveNext()){ ZipEntry e=en.Current as ZipEntry; if(e==null||!e.IsFile)continue; string n=Norm(e.Name); string low=n.ToLowerInvariant(); bool cloth=low.StartsWith("custom/clothing/"); bool hair=low.StartsWith("custom/hair/"); if(!cloth&&!hair)continue; if(low.EndsWith(".vam"))defs.Add(n); else if(low.EndsWith(".jpg")||low.EndsWith(".jpeg")||low.EndsWith(".png")){ string key=Norm(Path.ChangeExtension(n,null)); if(!previews.ContainsKey(key))previews[key]=n; } } for(int j=0;j<defs.Count;j++){ string def=defs[j]; WearableLite w=new WearableLite(); w.package=p; w.entryPath=def; w.name=Path.GetFileNameWithoutExtension(def); w.wearableType=Norm(def).StartsWith("Custom/Hair/",StringComparison.OrdinalIgnoreCase)?"Hair":"Clothing"; string key=Norm(Path.ChangeExtension(def,null)); string pv; if(previews.TryGetValue(key,out pv))w.previewEntry=pv; wearableItems.Add(w); } }catch{errors++;}finally{if(z!=null)z.Close();} } wearableItems.Sort((a,b)=>{int c=string.Compare(a.name,b.name,StringComparison.OrdinalIgnoreCase);return c!=0?c:string.Compare(a.package.uid,b.package.uid,StringComparison.OrdinalIgnoreCase);}); DebugLog("Wearable index built. items="+wearableItems.Count+", errors="+errors); }
    private List<WearableLite> FilteredWearables(){ EnsureWearableIndex(); List<WearableLite> l=new List<WearableLite>(); string q=searchQuery==null?"":searchQuery.Trim().ToLowerInvariant(); for(int i=0;i<wearableItems.Count;i++){WearableLite w=wearableItems[i];if(w.wearableType!=activeCat)continue;if(!MatchesAuthor(w.package))continue;if(q!=""&&w.name.ToLowerInvariant().IndexOf(q)<0&&w.package.uid.ToLowerInvariant().IndexOf(q)<0)continue;l.Add(w);}return l;}
    private List<VarPresetLite> FilteredVarPresets(){ var l=new List<VarPresetLite>(); string q=searchQuery==null?"":searchQuery.Trim().ToLowerInvariant(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); for(int i=0;i<varPresets.Count;i++){ VarPresetLite vp=varPresets[i]; if(vp==null || vp.package==null) continue; if(activeCat=="Favorites" && !favoriteUids.Contains(vp.package.uid)) continue; else if(activeCat!="Favorites" && !PresetTypeMatchesActiveCat(vp.presetType)) continue; if(!MatchesAuthor(vp.package)) continue; if(q!="" && vp.name.ToLowerInvariant().IndexOf(q)<0 && vp.entryPath.ToLowerInvariant().IndexOf(q)<0 && vp.package.uid.ToLowerInvariant().IndexOf(q)<0) continue; string key=Group(vp.package.uid)+"|"+vp.entryPath; if(!seen.Add(key)) continue; l.Add(vp); } return l; }
    private List<SceneLite> FilteredScenes(){ var l=new List<SceneLite>(); string q=searchQuery==null?"":searchQuery.Trim().ToLowerInvariant(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); for(int i=0;i<sceneItems.Count;i++){ SceneLite si=sceneItems[i]; if(si==null || si.package==null) continue; if(activeCat=="Favorites"){ if(!favoriteUids.Contains(si.package.uid) && !favoriteScenes.Contains(SceneRef(si.package, si.entryPath))) continue; } else if(activeCat!="Scenes" && activeCat!="Favorites") continue; if(!MatchesAuthor(si.package)) continue; if(q!="" && si.name.ToLowerInvariant().IndexOf(q)<0 && si.entryPath.ToLowerInvariant().IndexOf(q)<0 && si.package.uid.ToLowerInvariant().IndexOf(q)<0) continue; string key=Group(si.package.uid)+"|"+si.entryPath; if(!seen.Add(key)) continue; l.Add(si); } return l; }
    private void RefreshList(){
        if(listContent==null)return;
        StopThumbLoadCoroutine(); ClearListThumbs();
        var kids=new List<GameObject>(); foreach(Transform c in listContent) kids.Add(c.gameObject); foreach(var k in kids) Destroy(k);
        BuildSubBarButtons();
        UpdateSearchPlaceholder();
        if(activeCat=="Presets" || activeCat=="Clothing" || activeCat=="Hair" || activeCat=="Morphs" || (activeCat=="Favorites" && favSubCat=="Presets")) {
            RefreshPresetList(); RefreshPageStrip(); UpdateInspectorVisibility(); return;
        }
        if(activeCat=="Scenes" || (activeCat=="Favorites" && favSubCat=="Scenes")) {
            RefreshSceneList(); RefreshPageStrip(); UpdateInspectorVisibility(); return;
        }
        var l=Filtered();
        int max=l.Count==0?0:(l.Count-1)/pageSize;
        if(page<0)page=0; if(page>max)page=max;
        SaveCurrentPageState();
        int start=page*pageSize,end=Math.Min(start+pageSize,l.Count);
        // Keep selection only if still in filtered results; never auto-select first item.
        if(selected!=null && !ContainsPackageInList(l, selected.uid)) ClearSelectionKeepPreview(false);
        else if(selectedSceneItem!=null || selectedPreset!=null || selectedVarPreset!=null || selectedWearableItem!=null) {
            // package list view: clear non-package selections that no longer apply
            if(selectedSceneItem!=null || selectedPreset!=null || selectedVarPreset!=null || selectedWearableItem!=null) {
                // leave as-is if user switched away from those tabs via RefreshList package path
            }
        }
        List<KeyValuePair<PackageLite,Image>> thumbQueue=new List<KeyValuePair<PackageLite,Image>>();
        for(int i=start;i<end;i++){ Image thumbImg=CreatePackageCardNoThumb(l[i]); if(thumbImg!=null) thumbQueue.Add(new KeyValuePair<PackageLite,Image>(l[i],thumbImg)); }
        if(thumbQueue.Count>0) thumbLoadCoroutine = StartCoroutine(LoadThumbsAsync(thumbQueue));
        if(activeCat=="Favorites" && favSubCat=="All"){ List<PresetLite> fp=FilteredPresets(); for(int i=0;i<fp.Count && i<16;i++) CreatePresetCard(fp[i]); }
        ShowEmptyState(l.Count==0 && !(activeCat=="Favorites" && favSubCat=="All" && FilteredPresets().Count>0));
        UpdateResultToolbar(CatLabel(activeCat) + (activeCat=="Favorites"?" / "+FavSubLabel(favSubCat):""), l.Count, start, end, max);
        if(statusText!=null) statusText.text=OneLine(status,160);
        RefreshPageStrip();
        UpdateInspectorVisibility();
    }
    private void RefreshWearableList(){List<WearableLite> wl=FilteredWearables();int total=wl.Count;int max=total==0?0:(total-1)/pageSize;if(page<0)page=0;if(page>max)page=max;SaveCurrentPageState();int start=page*pageSize,end=Math.Min(start+pageSize,total);for(int i=start;i<end;i++)CreateWearableCard(wl[i]);if(selectedWearableItem!=null && !ContainsWearable(wl,selectedWearableItem)) ClearSelectionKeepPreview(false); ShowEmptyState(total==0); UpdateResultToolbar(CatLabel(activeCat), total, start, end, max); if(statusText!=null)statusText.text=OneLine(status,160); UpdateInspectorVisibility();}
    private void RefreshPresetList(){
        Stopwatch sw=Stopwatch.StartNew(); List<PresetLite> pl=FilteredPresets(); List<VarPresetLite> vpl=FilteredVarPresets(); int total=pl.Count+vpl.Count; int max=total==0?0:(total-1)/pageSize; if(page<0)page=0;if(page>max)page=max; SaveCurrentPageState(); int start=page*pageSize,end=Math.Min(start+pageSize,total);
        // Drop selection if no longer in results.
        if(selectedPreset!=null && !ContainsLocalPreset(pl, selectedPreset)) selectedPreset=null;
        if(selectedVarPreset!=null && !ContainsVarPreset(vpl, selectedVarPreset)) selectedVarPreset=null;
        if(selectedPreset==null && selectedVarPreset==null && (selected!=null || selectedSceneItem!=null || selectedWearableItem!=null)) { /* keep package selection only if still relevant */ }
        List<VarPresetThumbJob> thumbQueue=new List<VarPresetThumbJob>();
        for(int i=start;i<end;i++){ if(i<pl.Count) CreatePresetCard(pl[i]); else { VarPresetThumbJob job=CreateVarPresetCardNoThumb(vpl[i-pl.Count]); if(job!=null)thumbQueue.Add(job); } }
        if(thumbQueue.Count>0) thumbLoadCoroutine=StartCoroutine(LoadVarPresetThumbsAsync(thumbQueue));
        ShowEmptyState(total==0);
        UpdateResultToolbar(CatLabel(activeCat)+" · 本地"+pl.Count+" / 包内"+vpl.Count, total, start, end, max);
        if(statusText!=null) statusText.text=OneLine(status,160);
        sw.Stop(); DebugLog("RefreshPresetList shell built. total="+total+", shown="+(end-start)+", varThumbs="+thumbQueue.Count+", ms="+sw.Elapsed.TotalMilliseconds.ToString("0"));
    }
    private void RefreshSceneList(){
        Stopwatch sw=Stopwatch.StartNew();
        List<SceneLite> sl=FilteredScenes(); int total=sl.Count; int max=total==0?0:(total-1)/pageSize;
        if(page<0)page=0;if(page>max)page=max; SaveCurrentPageState(); int start=page*pageSize,end=Math.Min(start+pageSize,total);
        List<KeyValuePair<PackageLite,Image>> thumbQueue=new List<KeyValuePair<PackageLite,Image>>();
        for(int i=start;i<end;i++){ Image thumb=CreateSceneCard(sl[i]); if(thumb!=null)thumbQueue.Add(new KeyValuePair<PackageLite,Image>(sl[i].package,thumb)); }
        if(thumbQueue.Count>0)thumbLoadCoroutine=StartCoroutine(LoadThumbsAsync(thumbQueue));
        if(selectedSceneItem!=null && !ContainsScene(sl, selectedSceneItem)) ClearSelectionKeepPreview(false);
        ShowEmptyState(total==0); UpdateResultToolbar("场景" + (activeCat=="Favorites"?" / "+FavSubLabel(favSubCat):""), total, start, end, max);
        if(statusText!=null) statusText.text=OneLine(status,160);
        sw.Stop(); DebugLog("RefreshSceneList shell built. total="+total+", shown="+(end-start)+", thumbs="+thumbQueue.Count+", ms="+sw.Elapsed.TotalMilliseconds.ToString("0"));
    }

    private string FavSubLabel(string sub) { if(sub=="All") return "全部"; if(sub=="Scenes") return "场景"; if(sub=="Looks") return "外观包"; if(sub=="Scripts") return "脚本"; if(sub=="Presets") return "本地/包内预设"; return sub; }

    private void BuildFavSubBar() {
        // Sub bar is now built in BuildSubBarButtons, called from BuildPanel
        // This method is kept for RefreshList compatibility but does nothing in grid
        BuildSubBarButtons();
    }

        private void BuildSubBarButtons() {
        if (subBarRoot == null) return;
        CloseAuthorDropdown();
        favSubBtns.Clear();
        var kids = new List<GameObject>();
        foreach (Transform c in subBarRoot.transform) kids.Add(c.gameObject);
        foreach (var k in kids) Destroy(k);

        // Result count / section title
        GameObject labelObj = new GameObject("ResultCount");
        labelObj.transform.SetParent(subBarRoot.transform, false);
        LayoutElement le = labelObj.AddComponent<LayoutElement>();
        le.preferredWidth = 280; le.flexibleWidth = 1f; le.preferredHeight = 28;
        resultCountText = MakeText(labelObj.transform, "L", CatLabel(activeCat), 14, TextAnchor.MiddleLeft, colTextSecondary);
        resultCountText.rectTransform.anchorMin = Vector2.zero; resultCountText.rectTransform.anchorMax = Vector2.one;
        resultCountText.rectTransform.offsetMin = Vector2.zero; resultCountText.rectTransform.offsetMax = Vector2.zero;

        AddAuthorFilterControl();

        if (activeCat == "Favorites") {
            string[] subs = new string[]{"All","Scenes","Looks","Scripts","Presets"};
            for (int i = 0; i < subs.Length; i++) {
                string sub = subs[i];
                AddToolbarChip(FavSubLabel(sub), sub == favSubCat, () => {
                    favSubCat = sub; RestorePageForCurrentTab(); ClearSelectionKeepPreview(false); RefreshList();
                });
            }
        } else if (activeCat == "All") {
            AddToolbarChip("全部包", allSubFilter == "All", () => { allSubFilter = "All"; page = 0; SaveCurrentPageState(); ClearSelectionKeepPreview(false); RefreshList(); });
            AddToolbarChip("其他", allSubFilter == "Other", () => { allSubFilter = "Other"; page = 0; SaveCurrentPageState(); ClearSelectionKeepPreview(false); RefreshList(); });
        }

        // 页容量：桌面保留 2 个常用档；VR 默认固定，减少工具条噪音
        if (!isVRMode) {
            int[] sizes = new int[]{48, 96};
            for (int i = 0; i < sizes.Length; i++) {
                int n = sizes[i];
                AddToolbarChip(n + "/页", pageSize == n, () => SetPageSize(n));
            }
        }
    }

    private void AddAuthorFilterControl() {
        if (subBarRoot == null) return;
        string label = HasAuthorFilter() ? "作者：" + OneLine(authorFilter, 16) + " ▼" : "作者：全部 ▼";
        Button btn = MakeButton(subBarRoot.transform, label, 13, HasAuthorFilter() ? colAccentDim : colBtn);
        LayoutElement le = btn.gameObject.AddComponent<LayoutElement>();
        le.preferredWidth = isVRMode ? 190f : 160f;
        le.minWidth = isVRMode ? 160f : 140f;
        le.preferredHeight = isVRMode ? 36f : 28f;
        btn.onClick.AddListener(() => ToggleAuthorDropdown());
    }

    private Dictionary<string, int> GetAuthorCounts() {
        Dictionary<string, int> counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < all.Count; i++) {
            string author = PackageAuthor(all[i]);
            int n;
            counts.TryGetValue(author, out n);
            counts[author] = n + 1;
        }
        return counts;
    }

    private void ToggleAuthorDropdown() {
        if (authorDropdownRoot != null) { CloseAuthorDropdown(); return; }
        if (root == null) return;

        authorDropdownRoot = new GameObject("AuthorDropdown");
        authorDropdownRoot.transform.SetParent(root.transform, false);
        Image bg = authorDropdownRoot.AddComponent<Image>();
        bg.color = new Color(0.10f, 0.12f, 0.16f, 0.99f);
        RectTransform rt = authorDropdownRoot.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(isVRMode ? 0.14f : 0.12f, isVRMode ? 0.16f : 0.18f);
        rt.anchorMax = new Vector2(isVRMode ? 0.67f : 0.70f, isVRMode ? 0.84f : 0.82f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        authorDropdownRoot.transform.SetAsLastSibling();

        Text title = MakeText(authorDropdownRoot.transform, "Title", "按作者筛选", isVRMode ? 19 : 17, TextAnchor.MiddleLeft, colTextPrimary);
        RectTransform titleRt = title.rectTransform;
        titleRt.anchorMin = new Vector2(0, 1); titleRt.anchorMax = new Vector2(1, 1);
        titleRt.pivot = new Vector2(0.5f, 1); titleRt.offsetMin = new Vector2(14, -42); titleRt.offsetMax = new Vector2(-60, -6);

        Button close = MakeButton(authorDropdownRoot.transform, "×", 18, colBtn);
        RectTransform closeRt = close.GetComponent<RectTransform>();
        closeRt.anchorMin = new Vector2(1, 1); closeRt.anchorMax = new Vector2(1, 1);
        closeRt.pivot = new Vector2(1, 1); closeRt.anchoredPosition = new Vector2(-10, -8); closeRt.sizeDelta = new Vector2(38, 32);
        close.onClick.AddListener(() => CloseAuthorDropdown());

        GameObject searchObj = new GameObject("AuthorSearch");
        searchObj.transform.SetParent(authorDropdownRoot.transform, false);
        Image searchBg = searchObj.AddComponent<Image>(); searchBg.color = colScrollBg;
        RectTransform searchRt = searchObj.GetComponent<RectTransform>();
        searchRt.anchorMin = new Vector2(0, 1); searchRt.anchorMax = new Vector2(1, 1);
        searchRt.pivot = new Vector2(0.5f, 1); searchRt.offsetMin = new Vector2(12, -84); searchRt.offsetMax = new Vector2(-12, -48);
        authorDropdownSearchInput = searchObj.AddComponent<InputField>();
        Text inputText = MakeText(searchObj.transform, "Text", "", 14, TextAnchor.MiddleLeft, colTextPrimary);
        StretchFull(inputText.rectTransform, 10, 10, 4, 4);
        Text placeholder = MakeText(searchObj.transform, "Placeholder", "输入作者名筛选…", 14, TextAnchor.MiddleLeft, colTextDim);
        StretchFull(placeholder.rectTransform, 10, 10, 4, 4);
        authorDropdownSearchInput.textComponent = inputText;
        authorDropdownSearchInput.placeholder = placeholder;
        authorDropdownSearchInput.onValueChanged.AddListener((string value) => RefreshAuthorDropdownRows());

        GameObject scrollObj = new GameObject("AuthorScroll");
        scrollObj.transform.SetParent(authorDropdownRoot.transform, false);
        RectTransform scrollRt = scrollObj.AddComponent<RectTransform>();
        scrollRt.anchorMin = Vector2.zero; scrollRt.anchorMax = new Vector2(1, 1);
        scrollRt.offsetMin = new Vector2(12, 12); scrollRt.offsetMax = new Vector2(-12, -92);
        ScrollRect scroll = scrollObj.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.scrollSensitivity = 24f;
        Image scrollBg = scrollObj.AddComponent<Image>(); scrollBg.color = new Color(0.08f, 0.10f, 0.14f, 0.90f);
        GameObject viewport = new GameObject("Viewport");
        viewport.transform.SetParent(scrollObj.transform, false);
        RectTransform viewportRt = viewport.AddComponent<RectTransform>(); StretchFull(viewportRt, 2, 2, 2, 2);
        viewport.AddComponent<Image>().color = new Color(0, 0, 0, 0.01f);
        viewport.AddComponent<Mask>().showMaskGraphic = false;
        GameObject content = new GameObject("Content");
        content.transform.SetParent(viewport.transform, false);
        RectTransform contentRt = content.AddComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0, 1); contentRt.anchorMax = new Vector2(1, 1); contentRt.pivot = new Vector2(0.5f, 1);
        contentRt.offsetMin = Vector2.zero; contentRt.offsetMax = Vector2.zero;
        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 3; vlg.padding = new RectOffset(4, 4, 4, 4); vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childControlWidth = true; vlg.childControlHeight = false; vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = viewportRt; scroll.content = contentRt;
        authorDropdownContent = content.transform;
        RefreshAuthorDropdownRows();
    }

    private void CloseAuthorDropdown() {
        if (authorDropdownRoot != null) Destroy(authorDropdownRoot);
        authorDropdownRoot = null;
        authorDropdownContent = null;
        authorDropdownSearchInput = null;
    }

    private void RefreshAuthorDropdownRows() {
        if (authorDropdownContent == null) return;
        List<GameObject> old = new List<GameObject>();
        foreach (Transform c in authorDropdownContent) old.Add(c.gameObject);
        for (int i = 0; i < old.Count; i++) Destroy(old[i]);

        string query = authorDropdownSearchInput == null ? "" : (authorDropdownSearchInput.text ?? "").Trim();
        Dictionary<string, int> counts = GetAuthorCounts();
        List<string> authors = new List<string>(counts.Keys);
        authors.Sort(StringComparer.OrdinalIgnoreCase);
        AddAuthorDropdownRow("全部作者（" + all.Count + " 包）", "All", !HasAuthorFilter());
        int shown = 0;
        const int maxRows = 240;
        for (int i = 0; i < authors.Count; i++) {
            string author = authors[i];
            if (query.Length > 0 && author.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (shown >= maxRows) continue;
            AddAuthorDropdownRow(author + "（" + counts[author] + " 包）", author, string.Equals(author, authorFilter, StringComparison.OrdinalIgnoreCase));
            shown++;
        }
        if (shown == 0 && query.Length > 0) {
            Text empty = MakeText(authorDropdownContent, "Empty", "没有匹配的作者", 14, TextAnchor.MiddleCenter, colTextDim);
            SetFixedHeight(empty.gameObject, 34f);
        } else if (shown >= maxRows) {
            Text limit = MakeText(authorDropdownContent, "Limit", "匹配作者过多；请继续输入名称（最多显示 " + maxRows + " 项）", 12, TextAnchor.MiddleCenter, colTextDim);
            SetFixedHeight(limit.gameObject, 34f);
        }
    }

    private void AddAuthorDropdownRow(string label, string value, bool selectedValue) {
        Button btn = MakeButton(authorDropdownContent, label, isVRMode ? 15 : 14, selectedValue ? colAccentDim : colBtn);
        SetFixedHeight(btn.gameObject, isVRMode ? 42f : 34f);
        btn.onClick.AddListener(() => {
            authorFilter = value;
            page = 0;
            SaveCurrentPageState();
            SaveConfig();
            SetStatus(HasAuthorFilter() ? "作者筛选：" + authorFilter : "作者筛选：全部", false);
            CloseAuthorDropdown();
            ClearSelectionKeepPreview(false);
            RefreshList();
        });
    }

    private void AddToolbarChip(string label, bool selectedChip, UiAction action) {
        GameObject btnObj = new GameObject("Chip_" + label);
        btnObj.transform.SetParent(subBarRoot.transform, false);
        Image btnBg = btnObj.AddComponent<Image>();
        btnBg.color = selectedChip ? colAccent : colBtn;
        Button btn = btnObj.AddComponent<Button>();
        btn.targetGraphic = btnBg;
        LayoutElement le = btnObj.AddComponent<LayoutElement>();
        le.preferredWidth = Mathf.Max(56f, 12f + label.Length * 12f);
        le.preferredHeight = isVRMode ? 36f : 28f;
        Text txt = MakeText(btnObj.transform, "L", label, 13, TextAnchor.MiddleCenter, selectedChip ? Color.white : colTextPrimary);
        txt.rectTransform.anchorMin = Vector2.zero; txt.rectTransform.anchorMax = Vector2.one;
        txt.rectTransform.offsetMin = Vector2.zero; txt.rectTransform.offsetMax = Vector2.zero;
        btn.onClick.AddListener(() => { if (action != null) action(); });
        favSubBtns.Add(btn);
    }



    private void AddSubBarAction(string label, float width, UiAction action) {
        GameObject btnObj = new GameObject("VrAction_" + label);
        btnObj.transform.SetParent(subBarRoot.transform, false);
        Image bg = btnObj.AddComponent<Image>(); bg.color = colBtn;
        Button btn = btnObj.AddComponent<Button>(); btn.targetGraphic = bg;
        LayoutElement le = btnObj.AddComponent<LayoutElement>(); le.preferredWidth = width; le.preferredHeight = 30;
        Text txt = MakeText(btnObj.transform, "L", label, 13, TextAnchor.MiddleCenter, colTextPrimary);
        txt.rectTransform.anchorMin = Vector2.zero; txt.rectTransform.anchorMax = Vector2.one;
        txt.rectTransform.offsetMin = Vector2.zero; txt.rectTransform.offsetMax = Vector2.zero;
        btn.onClick.AddListener(() => { if (action != null) action(); });
    }

    private void SetPresetApplyMode(bool clothing, bool hair) {
        applyClothing = clothing;
        // 快捷模式一律带头发；只有“包含头发”开关可显式关闭
        // hair 参数保留兼容，刻意忽略“关发”快捷意图
        applyHair = true;
        if (hair) { /* keep true */ }
        if (applyClothingToggle != null) applyClothingToggle.isOn = applyClothing;
        if (applyHairToggle != null) applyHairToggle.isOn = applyHair;
        string mode = applyClothing ? "模型+服装+头发" : "模型+头发（不含服装）";
        SetStatus("预设加载模式：" + mode, true);
    }

    private void RefreshPageStrip() {
        if (pageStripRoot == null) return;
        List<GameObject> old = new List<GameObject>(); foreach (Transform c in pageStripRoot.transform) old.Add(c.gameObject); for (int i=0;i<old.Count;i++) Destroy(old[i]);
        int max = GetCurrentMaxPage();
        if (pageInfoText != null) pageInfoText.text = "第 " + (page + 1) + "/" + (max + 1) + " 页 · 每页 " + pageSize;
        if (max <= 0) return;
        // Compact window: up to 7 page numbers with ellipsis for edges.
        int window = 7;
        int from = Math.Max(0, page - window / 2);
        int to = Math.Min(max, from + window - 1);
        if (to - from + 1 < window) from = Math.Max(0, to - window + 1);
        List<int> pages = new List<int>();
        if (from > 0) { pages.Add(0); if (from > 1) pages.Add(-1); }
        for (int p = from; p <= to; p++) pages.Add(p);
        if (to < max) { if (to < max - 1) pages.Add(-1); pages.Add(max); }
        int last = -2;
        for (int i = 0; i < pages.Count; i++) {
            int p = pages[i];
            if (p == last) continue;
            last = p;
            if (p < 0) {
                Text dots = MakeText(pageStripRoot.transform, "Dots", "…", 12, TextAnchor.MiddleCenter, colTextDim);
                LayoutElement dle = dots.gameObject.AddComponent<LayoutElement>();
                dle.preferredWidth = 16; dle.preferredHeight = 28;
                continue;
            }
            Button b = MakeButton(pageStripRoot.transform, (p + 1).ToString(), 12, p == page ? colAccentDim : colBtn);
            LayoutElement le = b.gameObject.AddComponent<LayoutElement>();
            le.preferredWidth = isVRMode ? 44 : 32; le.preferredHeight = isVRMode ? 44 : 28;
            int target = p; b.onClick.AddListener(() => SetPageAbsolute(target));
        }
    }

    private bool ContainsPackageInRange(List<PackageLite> packages, string uid, int start, int end){ if(string.IsNullOrEmpty(uid)) return false; for(int i=start;i<end;i++) if(string.Equals(packages[i].uid,uid,StringComparison.OrdinalIgnoreCase)) return true; return false; }

        private void CreatePackageCard(PackageLite p){
        Image thumb = CreatePackageCardNoThumb(p);
        if (thumb == null) return;
        Texture2D tex; Sprite sp;
        if (TryLoadPackageSprite(p, 5L * 1024L * 1024L, out tex, out sp)) {
            thumb.sprite = sp; thumb.color = Color.white;
            listThumbTextures.Add(tex); listThumbSprites.Add(sp);
        } else {
            Text no = MakeText(thumb.transform, "NoThumb", "无预览", 12, TextAnchor.MiddleCenter, colTextDim);
            Rect(no.rectTransform, 0, 0, 154, 110);
        }
    }



        private Image CreatePackageCardNoThumb(PackageLite p){
        GameObject card = new GameObject("PkgCard_" + p.uid);
        card.transform.SetParent(listContent, false);
        bool isSelected = selected != null && string.Equals(selected.uid, p.uid, StringComparison.OrdinalIgnoreCase)
            && selectedPreset == null && selectedVarPreset == null && selectedSceneItem == null && selectedWearableItem == null;
        bool fav = IsFavorite(p);
        bool def = defaultUids.Contains(p.uid);

        Image bg = card.AddComponent<Image>();
        bg.color = isSelected ? colCardSelected : colCard;
        if (isSelected) {
            Outline ol = card.AddComponent<Outline>();
            ol.effectColor = colAccent;
            ol.effectDistance = new Vector2(2, -2);
        }
        Button cardBtn = card.AddComponent<Button>();
        cardBtn.targetGraphic = bg;
        ColorBlock cb = cardBtn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        cb.pressedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        cardBtn.colors = cb;
        cardBtn.onClick.AddListener(() => SelectPackage(p));
        card.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 190);

        GameObject thumbObj = new GameObject("Thumb");
        thumbObj.transform.SetParent(card.transform, false);
        Image thumb = thumbObj.AddComponent<Image>();
        thumb.color = colThumbBg;
        thumb.preserveAspect = true;
        Rect(thumb.rectTransform, 8, 8, 154, 110);

        Text title = MakeText(card.transform, "Title", ShortUid(p.uid), 13, TextAnchor.UpperLeft, colTextPrimary);
        title.verticalOverflow = VerticalWrapMode.Truncate;
        Rect(title.rectTransform, 10, 124, 120, 36);

        Text meta = MakeText(card.transform, "Meta", CatTextLabel(p).Replace("\uff0c", "/") + " · 依" + p.deps.Count, 11, TextAnchor.UpperLeft, colTextSecondary);
        Rect(meta.rectTransform, 10, 160, 150, 22);

        Button favBtn = MakeButton(card.transform, fav ? "★" : "☆", 16, fav ? colAccent : new Color(1,1,1,0.55f));
        Rect(favBtn.GetComponent<RectTransform>(), 132, 8, 30, 28);
        favBtn.onClick.AddListener(() => ToggleFavorite(p));

        if (def) {
            Text badge = MakeText(card.transform, "DefBadge", "保留", 10, TextAnchor.MiddleCenter, colAccent);
            Rect(badge.rectTransform, 8, 8, 36, 18);
        }

        return (p.thumbCache != "" || p.thumbEntry != "") ? thumb : null;
    }



    private IEnumerator LoadThumbsAsync(List<KeyValuePair<PackageLite,Image>> queue) {
        yield return null;
        Stopwatch sw = Stopwatch.StartNew();
        int perFrame = isVRMode ? 1 : 2;
        int count = 0;
        int loaded = 0;
        for (int i = 0; i < queue.Count; i++) {
            PackageLite p = queue[i].Key;
            Image thumb = queue[i].Value;
            if (thumb == null) continue;
            try {
                Texture2D tex; Sprite sp;
                if (TryLoadPackageSprite(p, 5L * 1024L * 1024L, out tex, out sp)) {
                    thumb.sprite = sp; thumb.color = Color.white;
                    listThumbTextures.Add(tex); listThumbSprites.Add(sp);
                    loaded++;
                }
            } catch {}
            count++;
            if (count >= perFrame) { count = 0; yield return null; }
        }
        sw.Stop();
        DebugLog("Package thumbs loaded. requested="+queue.Count+", loaded="+loaded+", perFrame="+perFrame+", ms="+sw.Elapsed.TotalMilliseconds.ToString("0"));
        thumbLoadCoroutine = null;
    }

        private void CreatePresetCard(PresetLite pr) {
        GameObject card = new GameObject("PresetCard_" + pr.name);
        card.transform.SetParent(listContent, false);
        bool fav = favoritePresets.Contains(pr.fullPath);
        bool isSelected = selectedPreset != null && string.Equals(selectedPreset.fullPath, pr.fullPath, StringComparison.OrdinalIgnoreCase);

        Image bg = card.AddComponent<Image>();
        bg.color = isSelected ? colCardSelected : colCard;
        if (isSelected) {
            Outline ol = card.AddComponent<Outline>();
            ol.effectColor = colAccent;
            ol.effectDistance = new Vector2(2, -2);
        }
        Button cardBtn = card.AddComponent<Button>();
        cardBtn.targetGraphic = bg;
        ColorBlock cb = cardBtn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        cb.pressedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        cardBtn.colors = cb;
        cardBtn.onClick.AddListener(() => SelectPreset(pr));
        card.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 190);

        GameObject thumbObj = new GameObject("Icon");
        thumbObj.transform.SetParent(card.transform, false);
        Image thumb = thumbObj.AddComponent<Image>();
        thumb.color = colThumbBg;
        Rect(thumb.rectTransform, 8, 8, 154, 110);
        Text iconTxt = MakeText(thumbObj.transform, "IconText", PresetTypeIcon(pr.presetType), 24, TextAnchor.MiddleCenter, colAccent);
        Rect(iconTxt.rectTransform, 0, 0, 154, 110);

        string thumbPath = Path.ChangeExtension(pr.fullPath, ".jpg");
        if (!File.Exists(thumbPath)) thumbPath = Path.ChangeExtension(pr.fullPath, ".png");
        if (File.Exists(thumbPath)) {
            try {
                byte[] data = File.ReadAllBytes(thumbPath);
                Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (tex.LoadImage(data)) {
                    Sprite sp = Sprite.Create(tex, new UnityEngine.Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f));
                    thumb.sprite = sp;
                    thumb.color = Color.white;
                    thumb.preserveAspect = true;
                    iconTxt.text = "";
                    listThumbTextures.Add(tex);
                    listThumbSprites.Add(sp);
                } else { UnityEngine.Object.Destroy(tex); }
            } catch {}
        }

        Text title = MakeText(card.transform, "Title", pr.name, 13, TextAnchor.UpperLeft, colTextPrimary);
        title.verticalOverflow = VerticalWrapMode.Truncate;
        Rect(title.rectTransform, 10, 124, 120, 36);

        Text meta = MakeText(card.transform, "Meta", pr.presetType + " · " + FormatSize(pr.size), 11, TextAnchor.UpperLeft, colTextSecondary);
        Rect(meta.rectTransform, 10, 160, 150, 22);

        Button favBtn = MakeButton(card.transform, fav ? "★" : "☆", 16, fav ? colAccent : new Color(1,1,1,0.55f));
        Rect(favBtn.GetComponent<RectTransform>(), 132, 8, 30, 28);
        favBtn.onClick.AddListener(() => TogglePresetFavorite(pr));
    }



    private void SelectPreset(PresetLite pr) {
        if (pr == null || details == null) return;
        LeaveSceneSelection();
        selected = null;
        selectedPreset = pr;
        selectedVarPreset = null;
        selectedSceneItem = null;
        selectedWearableItem = null;
        ClearPreview();
        details.text = pr.name + "\n\n类型：" + pr.presetType + "\n路径：" + pr.relPath + "\n大小：" + FormatSize(pr.size) + "\n收藏：" + (favoritePresets.Contains(pr.fullPath) ? "是" : "否") + "\n\n本地 .vap 预设文件";
        details.color = colTextSecondary;
        UpdateAtomSelectorUI();
        UpdateInspectorVisibility();
        SetStatus("已选择预设 " + pr.name, false);
    }

    private void TogglePresetFavorite(PresetLite pr) {
        if (pr == null) return;
        string favFile = pr.fullPath + ".fav";
        if (favoritePresets.Contains(pr.fullPath)) {
            favoritePresets.Remove(pr.fullPath);
            try { if (File.Exists(favFile)) File.Delete(favFile); } catch {}
            SetStatus("已取消收藏预设：" + pr.name, true);
        } else {
            favoritePresets.Add(pr.fullPath);
            try { File.WriteAllText(favFile, ""); } catch {}
            SetStatus("已收藏预设：" + pr.name, true);
        }
        SaveMarks();
        RefreshList();
    }

    private string PresetTypeIcon(string t) { if(t=="Appearance") return "[外观]"; if(t=="Clothing") return "[服装]"; if(t=="Hair") return "[头发]"; if(t=="Pose") return "[姿势]"; if(t=="General") return "[通用]"; if(t=="Skin") return "[皮肤]"; if(t=="Morphs") return "[形态]"; if(t=="Plugins") return "[插件]"; if(t=="Animation") return "[动画]"; if(t=="BreastPhysics") return "[胸物理]"; if(t=="GlutePhysics") return "[臀物理]"; if(t=="Full") return "[完整]"; return "[预设]"; }
    private string FormatSize(long bytes) { if(bytes<1024) return bytes+"B"; if(bytes<1048576) return (bytes/1024)+"KB"; return (bytes/1048576)+"MB"; }

        private VarPresetThumbJob CreateVarPresetCardNoThumb(VarPresetLite vp) {
        GameObject card = new GameObject("VarPresetCard_" + vp.package.uid + "_" + vp.name);
        card.transform.SetParent(listContent, false);
        bool isSelected = selectedVarPreset != null && selectedVarPreset.package != null
            && string.Equals(selectedVarPreset.package.uid, vp.package.uid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selectedVarPreset.entryPath, vp.entryPath, StringComparison.OrdinalIgnoreCase);

        Image bg = card.AddComponent<Image>();
        bg.color = isSelected ? colCardSelected : colCard;
        if (isSelected) {
            Outline ol = card.AddComponent<Outline>();
            ol.effectColor = colAccent;
            ol.effectDistance = new Vector2(2, -2);
        }
        Button cardBtn = card.AddComponent<Button>();
        cardBtn.targetGraphic = bg;
        ColorBlock cb = cardBtn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        cb.pressedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        cardBtn.colors = cb;
        cardBtn.onClick.AddListener(() => SelectVarPreset(vp));
        card.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 190);

        GameObject thumbObj = new GameObject("Icon");
        thumbObj.transform.SetParent(card.transform, false);
        Image thumb = thumbObj.AddComponent<Image>();
        thumb.color = colThumbBg;
        Rect(thumb.rectTransform, 8, 8, 154, 110);
        Text iconTxt = MakeText(thumbObj.transform, "IconText", PresetTypeIcon(vp.presetType), 24, TextAnchor.MiddleCenter, colAccent);
        Rect(iconTxt.rectTransform, 0, 0, 154, 110);

        Text title = MakeText(card.transform, "Title", vp.name, 13, TextAnchor.UpperLeft, colTextPrimary);
        title.verticalOverflow = VerticalWrapMode.Truncate;
        Rect(title.rectTransform, 10, 124, 120, 36);

        Text meta = MakeText(card.transform, "Meta", vp.presetType + " · " + ShortUid(vp.package.uid), 11, TextAnchor.UpperLeft, colTextSecondary);
        Rect(meta.rectTransform, 10, 160, 150, 22);

        bool fav = IsFavorite(vp.package);
        Button favBtn = MakeButton(card.transform, fav ? "★" : "☆", 16, fav ? colAccent : new Color(1,1,1,0.55f));
        Rect(favBtn.GetComponent<RectTransform>(), 132, 8, 30, 28);
        favBtn.onClick.AddListener(() => ToggleFavorite(vp.package));

        VarPresetThumbJob job = new VarPresetThumbJob(); job.preset=vp; job.image=thumb; job.iconText=iconTxt; return job;
    }



    private void CreateVarPresetCard(VarPresetLite vp) { CreateVarPresetCardNoThumb(vp); }

    private IEnumerator LoadVarPresetThumbsAsync(List<VarPresetThumbJob> queue) {
        Stopwatch sw=Stopwatch.StartNew(); int loaded=0;
        for(int i=0;i<queue.Count;i++){
            VarPresetThumbJob job=queue[i];
            if(job!=null && job.preset!=null && job.preset.package!=null && job.image!=null){
                try{
                    Texture2D tex; Sprite sp;
                    if(TryLoadPresetSpriteSingleZip(job.preset,5L*1024L*1024L,out tex,out sp)){
                        job.image.sprite=sp; job.image.color=Color.white; job.image.preserveAspect=true; if(job.iconText!=null)job.iconText.text="";
                        listThumbTextures.Add(tex); listThumbSprites.Add(sp); loaded++;
                    }
                }catch{}
            }
            yield return null;
        }
        sw.Stop(); DebugLog("Preset thumbs loaded. requested="+queue.Count+", loaded="+loaded+", ms="+sw.Elapsed.TotalMilliseconds.ToString("0")); thumbLoadCoroutine=null;
    }

    private bool TryLoadPresetSpriteSingleZip(VarPresetLite vp,long maxBytes,out Texture2D tex,out Sprite sp){
        tex=null; sp=null; ZipFile z=null;
        try{
            if(vp==null||vp.package==null)return false; z=new ZipFile(vp.package.fullPath); string basePath=Norm(Path.ChangeExtension(vp.entryPath,null)); string[] exts=new string[]{".jpg",".png",".jpeg"}; ZipEntry e=null;
            for(int i=0;i<exts.Length&&e==null;i++)e=FindEntry(z,basePath+exts[i]); if(e==null)return false; byte[] bytes=ReadEntryBytes(z,e,maxBytes); if(bytes==null||bytes.Length==0)return false;
            tex=new Texture2D(2,2,TextureFormat.RGBA32,false); if(!tex.LoadImage(bytes)){Destroy(tex);tex=null;return false;} sp=Sprite.Create(tex,new Rect(0,0,tex.width,tex.height),new Vector2(0.5f,0.5f)); return true;
        }catch{if(sp!=null)Destroy(sp);if(tex!=null)Destroy(tex);tex=null;sp=null;return false;}finally{if(z!=null)z.Close();}
    }

    private void SelectVarPreset(VarPresetLite vp) {
        if (vp == null || details == null) return;
        LeaveSceneSelection();
        selected = vp.package;
        selectedPreset = null;
        selectedVarPreset = vp;
        selectedSceneItem = null;
        selectedWearableItem = null;
        string previewEntry = GetPresetPreviewEntry(vp); LoadEntryPreview(vp.package, previewEntry);
        details.text = vp.name + "\n\n类型：" + vp.presetType + "预设\n包：" + vp.package.uid + "\n预设条目：" + vp.entryPath + "\n预览：" + (previewEntry==""?"无":previewEntry) + "\n大小：" + FormatSize(vp.package.size) + "\n收藏：" + (IsFavorite(vp.package) ? "是" : "否") + "\n\n这是 VaM " + vp.presetType + " Preset，会一次加载该预设中的整套内容。";
        details.color = colTextSecondary;
        UpdateAtomSelectorUI();
        UpdateInspectorVisibility();
        SetStatus("已选择包内预设 " + vp.name, false);
    }
    private void SelectVarPresetNoPreview(VarPresetLite vp) {
        if(vp==null||details==null)return; LeaveSceneSelection(); selected=vp.package; selectedPreset=null; selectedVarPreset=vp; selectedSceneItem=null; ClearPreview();
        details.text=vp.name+"\n\n类型："+vp.presetType+"预设\n包："+vp.package.uid+"\n预设条目："+vp.entryPath+"\n预览：点击卡片时加载\n大小："+FormatSize(vp.package.size)+"\n收藏："+(IsFavorite(vp.package)?"是":"否")+"\n\n这是 VaM "+vp.presetType+" Preset，会一次加载该预设中的整套内容。";
        details.color=colTextSecondary; UpdateAtomSelectorUI(); SetStatus("已选择包内预设 "+vp.name,false);
    }
    private string GetPresetPreviewEntry(VarPresetLite vp) { if(vp==null||vp.package==null)return ""; string basePath=Norm(Path.ChangeExtension(vp.entryPath,null)); string[] exts=new string[]{".jpg",".png",".jpeg"}; ZipFile z=null; try{z=new ZipFile(vp.package.fullPath);for(int i=0;i<exts.Length;i++){string candidate=basePath+exts[i];if(FindEntry(z,candidate)!=null)return candidate;}}catch{}finally{if(z!=null)z.Close();}return ""; }

        private void CreateWearableCard(WearableLite w) {
        GameObject card = new GameObject("WearableCard_" + w.package.uid + "_" + w.name);
        card.transform.SetParent(listContent, false);
        bool isSelected = selectedWearableItem != null && selectedWearableItem.package != null
            && string.Equals(selectedWearableItem.package.uid, w.package.uid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selectedWearableItem.entryPath, w.entryPath, StringComparison.OrdinalIgnoreCase);
        Image bg = card.AddComponent<Image>();
        bg.color = isSelected ? colCardSelected : colCard;
        if (isSelected) {
            Outline ol = card.AddComponent<Outline>();
            ol.effectColor = colAccent;
            ol.effectDistance = new Vector2(2, -2);
        }
        Button cardBtn = card.AddComponent<Button>();
        cardBtn.targetGraphic = bg;
        cardBtn.onClick.AddListener(() => SelectWearable(w));
        card.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 190);
        GameObject thumbObj = new GameObject("Thumb"); thumbObj.transform.SetParent(card.transform, false);
        Image thumb = thumbObj.AddComponent<Image>(); thumb.color = colThumbBg; thumb.preserveAspect = true;
        Rect(thumb.rectTransform, 8, 8, 154, 110);
        Texture2D tex; Sprite sp;
        if (TryLoadEntrySprite(w.package, w.previewEntry, 5L * 1024L * 1024L, out tex, out sp)) {
            thumb.sprite = sp; thumb.color = Color.white; listThumbTextures.Add(tex); listThumbSprites.Add(sp);
        } else {
            Text no = MakeText(thumbObj.transform, "NoPreview", w.wearableType == "Hair" ? "头发" : "服装", 18, TextAnchor.MiddleCenter, colTextDim);
            Rect(no.rectTransform, 0, 0, 154, 110);
        }
        Text title = MakeText(card.transform, "Title", w.name, 13, TextAnchor.UpperLeft, colTextPrimary);
        title.verticalOverflow = VerticalWrapMode.Truncate;
        Rect(title.rectTransform, 10, 124, 150, 36);
        Text meta = MakeText(card.transform, "Meta", ShortUid(w.package.uid), 11, TextAnchor.UpperLeft, colTextSecondary);
        Rect(meta.rectTransform, 10, 160, 150, 22);
    }


    private void SelectWearable(WearableLite w) {
        if (w == null) return; LeaveSceneSelection(); selected = w.package; selectedPreset = null; selectedVarPreset = null; selectedSceneItem = null; selectedWearableItem = w; LoadEntryPreview(w.package, w.previewEntry);
        if (details != null) { details.text = w.name + "\n\n类型：" + CatLabel(w.wearableType) + "\n包：" + w.package.uid + "\n定义：" + w.entryPath + "\n预览：" + (w.previewEntry == "" ? "无" : w.previewEntry) + "\n\n这是包内真实的服装/头发项目，不是场景。点击“链接”后可在 VaM 原生服装/头发选择器中使用。"; details.color = colTextSecondary; }
        UpdateAtomSelectorUI(); UpdateInspectorVisibility(); SetStatus("已选择" + CatLabel(w.wearableType) + " " + w.name, false);
    }
    private bool TryLoadEntrySprite(PackageLite p, string entry, long maxBytes, out Texture2D tex, out Sprite sp) {
        tex = null; sp = null; try { if (p == null || string.IsNullOrEmpty(entry)) return false; byte[] bytes = ReadBytes(p, entry, maxBytes); if (bytes == null || bytes.Length == 0) return false; tex = new Texture2D(2, 2, TextureFormat.RGBA32, false); if (!tex.LoadImage(bytes)) { Destroy(tex); tex = null; return false; } sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f)); return true; } catch { if (sp != null) Destroy(sp); if (tex != null) Destroy(tex); tex = null; sp = null; return false; }
    }
    private void LoadEntryPreview(PackageLite p, string entry) { ClearPreview(); if (preview == null) return; Texture2D tex; Sprite sp; if (!TryLoadEntrySprite(p, entry, 12L * 1024L * 1024L, out tex, out sp)) return; previewTex = tex; previewSprite = sp; preview.sprite = sp; preview.color = Color.white; }

        private Image CreateSceneCard(SceneLite si) {
        GameObject card = new GameObject("SceneCard_" + si.package.uid + "_" + si.name);
        card.transform.SetParent(listContent, false);
        bool fav = favoriteScenes.Contains(SceneRef(si.package, si.entryPath));
        bool isSelected = selectedSceneItem != null && selectedSceneItem.package != null
            && string.Equals(selectedSceneItem.package.uid, si.package.uid, StringComparison.OrdinalIgnoreCase)
            && string.Equals(selectedSceneItem.entryPath, si.entryPath, StringComparison.OrdinalIgnoreCase);

        Image bg = card.AddComponent<Image>();
        bg.color = isSelected ? colCardSelected : colCard;
        if (isSelected) {
            Outline ol = card.AddComponent<Outline>();
            ol.effectColor = colAccent;
            ol.effectDistance = new Vector2(2, -2);
        }
        Button cardBtn = card.AddComponent<Button>();
        cardBtn.targetGraphic = bg;
        ColorBlock cb = cardBtn.colors;
        cb.normalColor = Color.white;
        cb.highlightedColor = new Color(1.08f, 1.08f, 1.08f, 1f);
        cb.pressedColor = new Color(0.92f, 0.92f, 0.92f, 1f);
        cardBtn.colors = cb;
        cardBtn.onClick.AddListener(() => SelectSceneItem(si));
        card.GetComponent<RectTransform>().sizeDelta = new Vector2(170, 190);

        GameObject thumbObj = new GameObject("Thumb");
        thumbObj.transform.SetParent(card.transform, false);
        Image thumb = thumbObj.AddComponent<Image>();
        thumb.color = colThumbBg;
        thumb.preserveAspect = true;
        Rect(thumb.rectTransform, 8, 8, 154, 110);

        bool hasThumb = si.package != null && (!string.IsNullOrEmpty(si.package.thumbCache) || !string.IsNullOrEmpty(si.package.thumbEntry));
        if (!hasThumb) {
            Text no = MakeText(thumbObj.transform, "NoThumb", "无预览", 12, TextAnchor.MiddleCenter, colTextDim);
            Rect(no.rectTransform, 0, 0, 154, 110);
        }

        Text title = MakeText(card.transform, "Title", si.name, 13, TextAnchor.UpperLeft, colTextPrimary);
        title.verticalOverflow = VerticalWrapMode.Truncate;
        Rect(title.rectTransform, 10, 124, 120, 36);

        Text meta = MakeText(card.transform, "Meta", "场景 · " + ShortUid(si.package.uid), 11, TextAnchor.UpperLeft, colTextSecondary);
        Rect(meta.rectTransform, 10, 160, 150, 22);

        Button favBtn = MakeButton(card.transform, fav ? "★" : "☆", 16, fav ? colAccent : new Color(1,1,1,0.55f));
        Rect(favBtn.GetComponent<RectTransform>(), 132, 8, 30, 28);
        favBtn.onClick.AddListener(() => ToggleSceneFavoriteItem(si));
        return hasThumb ? thumb : null;
    }



    private void SelectSceneItem(SceneLite si) {
        if (si == null || details == null) return;
        StopScenePrewarm(true);
        selected = si.package;
        selectedPreset = null;
        selectedVarPreset = null;
        selectedSceneItem = si;
        selectedWearableItem = null;
        LoadPreview(si.package);
        selectedSceneAnalysis = ReadAndAnalyzeScene(si.package, si.entryPath, "");
        if (selectedSceneAnalysis != null && selectedSceneAnalysis.personIds.Count > 0
            && !selectedSceneAnalysis.personIds.Contains(scenePrimaryPersonId)) scenePrimaryPersonId = selectedSceneAnalysis.personIds[0];
        RefreshSelectedSceneDetails();
        UpdateAtomSelectorUI();
        UpdateInspectorVisibility();
        SetStatus("已选择场景 " + si.name, false);
        StartSelectedScenePrewarm();
    }

    private string SceneAnalysisKey(PackageLite p, string scene) {
        return (p == null ? "" : p.uid) + ":/" + Norm(scene ?? "");
    }

    private SceneJsonAnalysis ReadAndAnalyzeScene(PackageLite p, string scene, string knownJson) {
        string key = SceneAnalysisKey(p, scene);
        if (selectedSceneAnalysis != null && string.Equals(selectedSceneAnalysis.key, key, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(selectedSceneAnalysis.json)) return selectedSceneAnalysis;
        SceneJsonAnalysis analysis = new SceneJsonAnalysis();
        analysis.key = key;
        try {
            string json = knownJson;
            if (string.IsNullOrEmpty(json)) {
                string ignoredCachePath;
                if (!TryReadTimelineCache(p, scene, out json, out ignoredCachePath)) {
                    byte[] data = ReadBytes(p, scene, 128L * 1024L * 1024L);
                    if (data == null || data.Length == 0) { analysis.error = "场景 JSON 读取失败"; return analysis; }
                    json = Encoding.UTF8.GetString(data);
                }
            }
            analysis.json = json;
            string error;
            if (!TryAnalyzeSceneAtoms(json, analysis, out error)) analysis.error = error;
            DebugLog("Scene analysis: key=" + key + ", bytes=" + Encoding.UTF8.GetByteCount(json) + ", atoms=" + analysis.atoms.Count + ", persons=" + analysis.personIds.Count + ", error=" + analysis.error);
        } catch(Exception e) {
            analysis.error = e.Message;
            DebugLog("Scene analysis failed: key=" + key + " | " + e.ToString());
        }
        return analysis;
    }

    private void RefreshSelectedSceneDetails() {
        if (selectedSceneItem == null || details == null) return;
        SceneLite si = selectedSceneItem;
        StringBuilder sb = new StringBuilder();
        sb.Append(si.name).Append("\n\n类型：Scene\n包：").Append(si.package.uid)
          .Append("\n条目：").Append(si.entryPath)
          .Append("\n收藏：").Append(favoriteScenes.Contains(SceneRef(si.package, si.entryPath)) ? "是" : "否");
        if (selectedSceneAnalysis != null) {
            if (string.IsNullOrEmpty(selectedSceneAnalysis.error)) {
                sb.Append("\nAtom：").Append(selectedSceneAnalysis.atoms.Count)
                  .Append("  人物：").Append(selectedSceneAnalysis.personIds.Count);
                if (!string.IsNullOrEmpty(scenePrimaryPersonId)) sb.Append("\n主角：").Append(scenePrimaryPersonId);
                sb.Append("  模式：").Append(SceneLoadModeName(sceneLoadMode));
            } else sb.Append("\n分析失败：").Append(selectedSceneAnalysis.error);
        }
        details.text = sb.ToString();
        details.color = colTextSecondary;
        UpdateSceneLoadModeUI();
    }

    private string SceneLoadModeName(int mode) {
        if (mode == 1) return "人物优先";
        if (mode == 2) return "极简人物";
        return "完整";
    }

    private void SetSceneLoadMode(int mode) {
        sceneLoadMode = Mathf.Clamp(mode, 0, 2);
        SaveConfig();
        UpdateSceneLoadModeUI();
        RefreshSelectedSceneDetails();
        SetStatus("场景加载模式：" + SceneLoadModeName(sceneLoadMode), true);
    }

    private void UpdateSceneLoadModeUI() {
        SetModeButtonColor(sceneFullModeBtn, sceneLoadMode == 0);
        SetModeButtonColor(scenePrimaryModeBtn, sceneLoadMode == 1);
        SetModeButtonColor(sceneMinimalModeBtn, sceneLoadMode == 2);
        if (scenePrimaryPersonLabel != null) scenePrimaryPersonLabel.text = "主角：" + (string.IsNullOrEmpty(scenePrimaryPersonId) ? "自动" : scenePrimaryPersonId);
    }

    private void SetModeButtonColor(Button button, bool selectedMode) {
        if (button == null) return;
        Image bg = button.GetComponent<Image>();
        if (bg != null) bg.color = selectedMode ? colAccentDim : colBtn;
    }

    private void CycleScenePrimaryPerson() {
        if (selectedSceneAnalysis == null || selectedSceneAnalysis.personIds.Count == 0) {
            SetStatus("该场景没有可选择的人物 Atom。", false);
            return;
        }
        int idx = selectedSceneAnalysis.personIds.IndexOf(scenePrimaryPersonId);
        idx = (idx + 1) % selectedSceneAnalysis.personIds.Count;
        scenePrimaryPersonId = selectedSceneAnalysis.personIds[idx];
        RefreshSelectedSceneDetails();
        StartSelectedScenePrewarm();
        SetStatus("场景主角：" + scenePrimaryPersonId, true);
    }

    private void StartSelectedScenePrewarm() {
        StopScenePrewarm(true);
        if (!sceneTexturePrewarmEnabled || selectedSceneItem == null || selectedSceneAnalysis == null
            || !string.IsNullOrEmpty(selectedSceneAnalysis.error) || string.IsNullOrEmpty(scenePrimaryPersonId)) return;
        if (autoCleanLinksBeforeSceneLoad) {
            DebugLog("Scene prewarm skipped because autoCleanLinksBeforeSceneLoad is enabled.");
            return;
        }
        int generation = scenePrewarmGeneration;
        string key = selectedSceneAnalysis.key;
        scenePrewarmCoroutine = StartCoroutine(PrewarmSelectedSceneCoroutine(generation, key));
    }

    private IEnumerator PrewarmSelectedSceneCoroutine(int generation, string key) {
        yield return new WaitForSecondsRealtime(0.75f);
        scenePrewarmCoroutine = null;
        if (generation != scenePrewarmGeneration || !sceneTexturePrewarmEnabled || selectedSceneAnalysis == null
            || !string.Equals(selectedSceneAnalysis.key, key, StringComparison.OrdinalIgnoreCase) || selectedSceneItem == null) yield break;
        Stopwatch sw = Stopwatch.StartNew();
        int linked = 0, already = 0, missing = 0, errors = 0;
        try {
            LinkResult rootResult = LinkWithDeps(selectedSceneItem.package);
            PresetLinkDiag directResult = AutoLinkSceneDepsDetailed(selectedSceneAnalysis.json);
            linked = rootResult.created + directResult.linked;
            already = rootResult.already + directResult.already;
            missing = rootResult.missing.Count + directResult.missing.Count;
            errors = rootResult.errors.Count + directResult.errors.Count;
            if (linked > 0) RefreshVam();
        } catch(Exception e) {
            errors++;
            DebugLog("Scene prewarm prelink failed: " + e.ToString());
        }
        yield return null;
        if (generation != scenePrewarmGeneration || selectedSceneAnalysis == null
            || !string.Equals(selectedSceneAnalysis.key, key, StringComparison.OrdinalIgnoreCase)) yield break;
        string scenePackageUid = selectedSceneItem == null || selectedSceneItem.package == null ? "" : selectedSceneItem.package.uid;
        int queued = QueuePrimaryPersonSkinPrewarm(selectedSceneAnalysis, scenePrimaryPersonId, scenePackageUid, generation);
        sw.Stop();
        scenePrewarmKey = key;
        DebugLog("Scene prewarm queued: key=" + key + ", person=" + scenePrimaryPersonId + ", textures=" + queued + ", linked=" + linked + ", already=" + already + ", missing=" + missing + ", errors=" + errors + ", prepMs=" + sw.Elapsed.TotalMilliseconds.ToString("0"));
        if (queued > 0) SetStatus("正在预热 " + scenePrimaryPersonId + " 的 " + queued + " 张皮肤纹理...", false);
        else if (errors == 0) SetStatus("场景依赖已预链接，人物皮肤已在缓存或无需预热。", false);
    }

    private void StopScenePrewarm(bool cancelQueued) {
        scenePrewarmGeneration++;
        if (scenePrewarmCoroutine != null) {
            try { StopCoroutine(scenePrewarmCoroutine); } catch {}
            scenePrewarmCoroutine = null;
        }
        if (cancelQueued) {
            for (int i = 0; i < activePrewarmImages.Count; i++) {
                try { if (activePrewarmImages[i] != null && !activePrewarmImages[i].processed) activePrewarmImages[i].cancel = true; } catch {}
            }
        }
        activePrewarmImages.Clear();
        activePrewarmSignatures.Clear();
        scenePrewarmPending = 0;
        scenePrewarmErrors = 0;
        scenePrewarmKey = "";
    }

    private int QueuePrimaryPersonSkinPrewarm(SceneJsonAnalysis analysis, string personId, string scenePackageUid, int generation) {
        if (analysis == null || ImageLoaderThreaded.singleton == null || string.IsNullOrEmpty(personId)) return 0;
        SceneAtomSpan person = null;
        for (int i = 0; i < analysis.atoms.Count; i++) {
            if (analysis.atoms[i].type == "Person" && string.Equals(analysis.atoms[i].id, personId, StringComparison.Ordinal)) { person = analysis.atoms[i]; break; }
        }
        if (person == null) return 0;
        int queued = 0;
        queued += QueueSkinFields(analysis.json, person, new string[] { "faceDiffuseUrl", "torsoDiffuseUrl", "limbsDiffuseUrl", "genitalsDiffuseUrl", "faceDecalUrl", "torsoDecalUrl", "limbsDecalUrl", "genitalsDecalUrl" }, false, false, scenePackageUid, generation, MaxScenePrewarmTextures - queued);
        queued += QueueSkinFields(analysis.json, person, new string[] { "faceSpecularUrl", "torsoSpecularUrl", "limbsSpecularUrl", "genitalsSpecularUrl", "faceGlossUrl", "torsoGlossUrl", "limbsGlossUrl", "genitalsGlossUrl" }, true, false, scenePackageUid, generation, MaxScenePrewarmTextures - queued);
        queued += QueueSkinFields(analysis.json, person, new string[] { "faceNormalUrl", "torsoNormalUrl", "limbsNormalUrl", "genitalsNormalUrl", "faceDetailUrl", "torsoDetailUrl", "limbsDetailUrl", "genitalsDetailUrl" }, true, true, scenePackageUid, generation, MaxScenePrewarmTextures - queued);
        return queued;
    }

    private int QueueSkinFields(string json, SceneAtomSpan atom, string[] fields, bool linear, bool normal, string scenePackageUid, int generation, int maxToQueue) {
        int queued = 0;
        if (maxToQueue <= 0) return 0;
        for (int i = 0; i < fields.Length; i++) {
            List<string> values = FindJsonStringPropertyValues(json, atom.start, atom.start + atom.length, fields[i]);
            for (int v = 0; v < values.Count; v++) {
                if (QueueSkinTexturePrewarm(values[v], linear, normal, scenePackageUid, generation)) queued++;
                if (queued >= maxToQueue) return queued;
            }
        }
        return queued;
    }

    private bool QueueSkinTexturePrewarm(string path, bool linear, bool normal, string scenePackageUid, int generation) {
        if (string.IsNullOrEmpty(path) || path == "NULL" || ImageLoaderThreaded.singleton == null) return false;
        path = ResolveSceneTexturePrewarmPath(path, scenePackageUid);
        ImageLoaderThreaded.QueuedImage qi = new ImageLoaderThreaded.QueuedImage();
        qi.imgPath = path;
        qi.createMipMaps = true;
        qi.compress = true;
        qi.linear = linear;
        qi.isNormalMap = normal;
        string signature = qi.cacheSignature;
        try {
            if (ImageLoaderThreaded.singleton.IsTextureCached(signature) || activePrewarmSignatures.Contains(signature)) return false;
            activePrewarmSignatures.Add(signature);
            activePrewarmImages.Add(qi);
            scenePrewarmPending++;
            qi.callback = delegate(ImageLoaderThreaded.QueuedImage loaded) { OnSceneTexturePrewarmed(loaded, signature, generation); };
            ImageLoaderThreaded.singleton.PreloadImage(qi);
            // PreloadImage does not invoke callbacks when the resolved texture is already cached.
            if (ImageLoaderThreaded.singleton.IsTextureCached(qi.cacheSignature)) {
                qi.callback = null;
                activePrewarmSignatures.Remove(signature);
                activePrewarmImages.Remove(qi);
                scenePrewarmPending = Math.Max(0, scenePrewarmPending - 1);
                return false;
            }
            return true;
        } catch(Exception e) {
            activePrewarmSignatures.Remove(signature);
            activePrewarmImages.Remove(qi);
            scenePrewarmPending = Math.Max(0, scenePrewarmPending - 1);
            DebugLog("Texture prewarm queue failed: " + signature + " | " + e.Message);
            return false;
        }
    }

    private static string ResolveSceneTexturePrewarmPath(string path, string scenePackageUid) {
        const string selfPrefix = "SELF:/";
        if (!string.IsNullOrEmpty(scenePackageUid) && path.StartsWith(selfPrefix, StringComparison.OrdinalIgnoreCase))
            path = scenePackageUid + ":/" + path.Substring(selfPrefix.Length);
        if (path.IndexOf(".latest:", StringComparison.OrdinalIgnoreCase) >= 0) {
            try {
                string normalized = FileManager.NormalizeLoadPath(path);
                if (!string.IsNullOrEmpty(normalized)) path = normalized;
            } catch {}
        }
        return path;
    }

    private void OnSceneTexturePrewarmed(ImageLoaderThreaded.QueuedImage image, string signature, int generation) {
        if (generation != scenePrewarmGeneration) return;
        if (image == null || image.hadError) {
            scenePrewarmErrors++;
            DebugLog("Scene texture prewarm failed: " + signature + (image == null ? "" : " | " + image.errorText));
        }
        activePrewarmSignatures.Remove(signature);
        activePrewarmImages.Remove(image);
        scenePrewarmPending = Math.Max(0, scenePrewarmPending - 1);
        if (scenePrewarmPending == 0) {
            DebugLog("Scene prewarm complete: key=" + scenePrewarmKey);
            SetStatus(scenePrewarmErrors == 0 ? "主人物皮肤预热完成。" : "主人物皮肤预热完成，失败 " + scenePrewarmErrors + " 张。", scenePrewarmErrors > 0);
        }
    }

    private static bool TryAnalyzeSceneAtoms(string json, SceneJsonAnalysis analysis, out string error) {
        error = "";
        if (analysis == null || string.IsNullOrEmpty(json)) { error = "场景 JSON 为空"; return false; }
        int atomsOpen;
        if (!TryFindTopLevelArrayProperty(json, "atoms", out atomsOpen)) { error = "未找到顶层 atoms 数组"; return false; }
        int atomsClose = FindMatchingJsonContainer(json, atomsOpen, '[', ']');
        if (atomsClose < 0) { error = "atoms 数组没有闭合"; return false; }
        analysis.atomsOpen = atomsOpen;
        analysis.atomsClose = atomsClose;
        int cursor = atomsOpen + 1;
        while (cursor < atomsClose) {
            while (cursor < atomsClose && (char.IsWhiteSpace(json[cursor]) || json[cursor] == ',')) cursor++;
            if (cursor >= atomsClose) break;
            if (json[cursor] != '{') { error = "atoms 数组包含非对象元素，位置 " + cursor; return false; }
            int objectClose = FindMatchingJsonContainer(json, cursor, '{', '}');
            if (objectClose < 0 || objectClose > atomsClose) { error = "Atom 对象没有闭合，位置 " + cursor; return false; }
            SceneAtomSpan atom = new SceneAtomSpan();
            atom.start = cursor;
            atom.length = objectClose - cursor + 1;
            TryFindDirectStringProperty(json, cursor, objectClose + 1, "id", out atom.id);
            TryFindDirectStringProperty(json, cursor, objectClose + 1, "type", out atom.type);
            analysis.atoms.Add(atom);
            if (string.Equals(atom.type, "Person", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(atom.id)) analysis.personIds.Add(atom.id);
            cursor = objectClose + 1;
        }
        return true;
    }

    private static bool TryFindTopLevelArrayProperty(string json, string property, out int arrayOpen) {
        arrayOpen = -1;
        int objectDepth = 0, arrayDepth = 0, cursor = 0;
        while (cursor < json.Length) {
            char c = json[cursor];
            if (c == '"') {
                string token; int next;
                if (!TryReadJsonString(json, cursor, json.Length, out token, out next)) return false;
                if (objectDepth == 1 && arrayDepth == 0 && string.Equals(token, property, StringComparison.Ordinal)) {
                    int p = next;
                    while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                    if (p < json.Length && json[p] == ':') p++;
                    while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                    if (p < json.Length && json[p] == '[') { arrayOpen = p; return true; }
                }
                cursor = next;
                continue;
            }
            if (c == '{') objectDepth++;
            else if (c == '}') objectDepth--;
            else if (c == '[') arrayDepth++;
            else if (c == ']') arrayDepth--;
            cursor++;
        }
        return false;
    }

    private static int FindMatchingJsonContainer(string json, int openAt, char open, char close) {
        int depth = 0, cursor = openAt;
        while (cursor < json.Length) {
            char c = json[cursor];
            if (c == '"') {
                string ignored; int next;
                if (!TryReadJsonString(json, cursor, json.Length, out ignored, out next)) return -1;
                cursor = next;
                continue;
            }
            if (c == open) depth++;
            else if (c == close) {
                depth--;
                if (depth == 0) return cursor;
            }
            cursor++;
        }
        return -1;
    }

    private static bool TryFindDirectStringProperty(string json, int start, int end, string property, out string value) {
        value = "";
        int objectDepth = 0, arrayDepth = 0, cursor = start;
        while (cursor < end) {
            char c = json[cursor];
            if (c == '"') {
                string token; int next;
                if (!TryReadJsonString(json, cursor, end, out token, out next)) return false;
                if (objectDepth == 1 && arrayDepth == 0 && string.Equals(token, property, StringComparison.Ordinal)) {
                    int p = next;
                    while (p < end && char.IsWhiteSpace(json[p])) p++;
                    if (p < end && json[p] == ':') p++;
                    while (p < end && char.IsWhiteSpace(json[p])) p++;
                    int after;
                    if (p < end && json[p] == '"' && TryReadJsonString(json, p, end, out value, out after)) return true;
                }
                cursor = next;
                continue;
            }
            if (c == '{') objectDepth++;
            else if (c == '}') objectDepth--;
            else if (c == '[') arrayDepth++;
            else if (c == ']') arrayDepth--;
            cursor++;
        }
        return false;
    }

    private static bool TryReadJsonString(string json, int quoteAt, int limit, out string value, out int next) {
        value = ""; next = quoteAt;
        if (quoteAt < 0 || quoteAt >= limit || json[quoteAt] != '"') return false;
        StringBuilder sb = new StringBuilder();
        int cursor = quoteAt + 1;
        while (cursor < limit) {
            char c = json[cursor++];
            if (c == '"') { value = sb.ToString(); next = cursor; return true; }
            if (c != '\\') { sb.Append(c); continue; }
            if (cursor >= limit) return false;
            char esc = json[cursor++];
            if (esc == '"' || esc == '\\' || esc == '/') sb.Append(esc);
            else if (esc == 'b') sb.Append('\b');
            else if (esc == 'f') sb.Append('\f');
            else if (esc == 'n') sb.Append('\n');
            else if (esc == 'r') sb.Append('\r');
            else if (esc == 't') sb.Append('\t');
            else if (esc == 'u') {
                if (cursor + 4 > limit) return false;
                int code = 0;
                for (int i = 0; i < 4; i++) {
                    char h = json[cursor++];
                    int digit = h >= '0' && h <= '9' ? h - '0' : h >= 'a' && h <= 'f' ? h - 'a' + 10 : h >= 'A' && h <= 'F' ? h - 'A' + 10 : -1;
                    if (digit < 0) return false;
                    code = code * 16 + digit;
                }
                sb.Append((char)code);
            } else return false;
        }
        return false;
    }

    private static List<string> FindJsonStringPropertyValues(string json, int start, int end, string property) {
        List<string> result = new List<string>();
        int cursor = Math.Max(0, start);
        end = Math.Min(end, json == null ? 0 : json.Length);
        while (cursor < end) {
            if (json[cursor] != '"') { cursor++; continue; }
            string key; int next;
            if (!TryReadJsonString(json, cursor, end, out key, out next)) break;
            int p = next;
            while (p < end && char.IsWhiteSpace(json[p])) p++;
            if (p < end && json[p] == ':' && string.Equals(key, property, StringComparison.OrdinalIgnoreCase)) {
                p++;
                while (p < end && char.IsWhiteSpace(json[p])) p++;
                string value; int after;
                if (p < end && json[p] == '"' && TryReadJsonString(json, p, end, out value, out after)) {
                    if (!string.IsNullOrEmpty(value) && !result.Contains(value)) result.Add(value);
                    cursor = after;
                    continue;
                }
            }
            cursor = next;
        }
        return result;
    }

    private static HashSet<string> FindAtomReferences(string json, SceneAtomSpan atom, HashSet<string> knownIds) {
        HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> referenceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "receiverAtom", "parentAtom", "atom", "atomUid", "atomUID", "sourceAtom" };
        int cursor = atom.start, end = atom.start + atom.length;
        while (cursor < end) {
            if (json[cursor] != '"') { cursor++; continue; }
            string key; int next;
            if (!TryReadJsonString(json, cursor, end, out key, out next)) break;
            int p = next;
            while (p < end && char.IsWhiteSpace(json[p])) p++;
            if (p < end && json[p] == ':' && referenceKeys.Contains(key)) {
                p++;
                while (p < end && char.IsWhiteSpace(json[p])) p++;
                string value; int after;
                if (p < end && json[p] == '"' && TryReadJsonString(json, p, end, out value, out after)) {
                    if (knownIds.Contains(value)) result.Add(value);
                    cursor = after;
                    continue;
                }
            }
            cursor = next;
        }
        return result;
    }

    private static bool TryBuildSceneVariants(SceneJsonAnalysis analysis, int mode, string primaryPersonId, out SceneVariantResult result, out string error) {
        result = new SceneVariantResult(); error = "";
        if (analysis == null || !string.IsNullOrEmpty(analysis.error) || analysis.atomsOpen < 0 || analysis.atomsClose < 0) { error = "场景结构尚未成功分析"; return false; }
        if (mode <= 0) { result.primaryJson = analysis.json; result.totalAtoms = result.keptAtoms = analysis.atoms.Count; return true; }
        SceneAtomSpan primary = null;
        HashSet<string> knownIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < analysis.atoms.Count; i++) {
            if (!string.IsNullOrEmpty(analysis.atoms[i].id)) knownIds.Add(analysis.atoms[i].id);
            if (string.Equals(analysis.atoms[i].type, "Person", StringComparison.OrdinalIgnoreCase) && string.Equals(analysis.atoms[i].id, primaryPersonId, StringComparison.Ordinal)) primary = analysis.atoms[i];
        }
        if (primary == null) { error = "没有找到主角 Person：" + primaryPersonId; return false; }
        HashSet<string> related = new HashSet<string>(StringComparer.Ordinal);
        if (mode == 1) {
            for (int i = 0; i < analysis.atoms.Count; i++) {
                SceneAtomSpan atom = analysis.atoms[i];
                if (!string.Equals(atom.type, "Person", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(atom.id)) continue;
                related.Add(atom.id);
                related.UnionWith(FindAtomReferences(analysis.json, atom, knownIds));
            }
            bool changed = true;
            while (changed) {
                changed = false;
                for (int i = 0; i < analysis.atoms.Count; i++) {
                    SceneAtomSpan atom = analysis.atoms[i];
                    if (related.Contains(atom.id)) continue;
                    List<string> parents = FindJsonStringPropertyValues(analysis.json, atom.start, atom.start + atom.length, "parentAtom");
                    for (int p = 0; p < parents.Count; p++) {
                        if (related.Contains(parents[p])) { related.Add(atom.id); changed = true; break; }
                    }
                }
            }
        } else {
            related.UnionWith(FindAtomReferences(analysis.json, primary, knownIds));
            related.Add(primaryPersonId);
        }
        List<SceneAtomSpan> kept = new List<SceneAtomSpan>();
        List<SceneAtomSpan> deferred = new List<SceneAtomSpan>();
        Dictionary<string,int> deferredTypeCounts = new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < analysis.atoms.Count; i++) {
            SceneAtomSpan atom = analysis.atoms[i];
            bool keep = ShouldKeepSceneAtom(atom, mode, primaryPersonId, related);
            if (keep) kept.Add(atom);
            else {
                deferred.Add(atom);
                string type = string.IsNullOrEmpty(atom.type) ? "Unknown" : atom.type;
                int count; deferredTypeCounts.TryGetValue(type, out count); deferredTypeCounts[type] = count + 1;
            }
        }
        result.totalAtoms = analysis.atoms.Count;
        result.keptAtoms = kept.Count;
        result.deferredAtoms = deferred.Count;
        result.primaryJson = BuildSceneJsonWithAtoms(analysis, kept);
        if (deferred.Count > 0) result.deferredJson = BuildSceneJsonWithAtoms(analysis, deferred);
        List<string> types = new List<string>(deferredTypeCounts.Keys);
        types.Sort(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < types.Count; i++) result.deferredTypes.Add(types[i] + "x" + deferredTypeCounts[types[i]]);
        return true;
    }

    private static bool ShouldKeepSceneAtom(SceneAtomSpan atom, int mode, string primaryPersonId, HashSet<string> related) {
        string type = atom.type ?? "";
        if (string.Equals(type, "Person", StringComparison.OrdinalIgnoreCase)) return mode == 1 || string.Equals(atom.id, primaryPersonId, StringComparison.Ordinal);
        if (IsSystemSceneAtomType(type) || IsLightSceneAtomType(type)) return true;
        if (mode >= 2) return false;
        if (related.Contains(atom.id)) return true;
        return !IsHeavyOptionalSceneAtomType(type);
    }

    private static bool IsSystemSceneAtomType(string type) {
        return string.Equals(type, "WindowCamera", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "PlayerNavigationPanel", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "CoreControl", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "VRController", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLightSceneAtomType(string type) {
        return !string.IsNullOrEmpty(type) && type.EndsWith("Light", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeavyOptionalSceneAtomType(string type) {
        return string.Equals(type, "CustomUnityAsset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "SubScene", StringComparison.OrdinalIgnoreCase)
            || type.IndexOf("AudioSource", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("Video", StringComparison.OrdinalIgnoreCase) >= 0
            || type.IndexOf("Browser", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string BuildSceneJsonWithAtoms(SceneJsonAnalysis analysis, List<SceneAtomSpan> atoms) {
        int capacity = analysis.atomsOpen + 1 + (analysis.json.Length - analysis.atomsClose) + Math.Max(0, atoms.Count - 1);
        for (int i = 0; i < atoms.Count; i++) capacity += atoms[i].length;
        StringBuilder sb = new StringBuilder(capacity);
        sb.Append(analysis.json, 0, analysis.atomsOpen + 1);
        for (int i = 0; i < atoms.Count; i++) {
            if (i > 0) sb.Append(',');
            sb.Append(analysis.json, atoms[i].start, atoms[i].length);
        }
        sb.Append(analysis.json, analysis.atomsClose, analysis.json.Length - analysis.atomsClose);
        return sb.ToString();
    }

    private void ToggleSceneFavoriteItem(SceneLite si) {
        if (si == null || si.package == null) return;
        string sr = SceneRef(si.package, si.entryPath);
        if (favoriteScenes.Contains(sr)) {
            favoriteScenes.Remove(sr);
            SetStatus("已取消收藏场景：" + si.name, true);
        } else {
            favoriteScenes.Add(sr);
            SetStatus("已收藏场景：" + si.name, true);
        }
        SaveMarks();
        RefreshList();
    }

    private List<string> GetPersonAtomUids() {
        List<string> uids = new List<string>();
        try {
            if (SuperController.singleton == null) return uids;
            List<Atom> atoms = SuperController.singleton.GetAtoms();
            for (int i = 0; i < atoms.Count; i++) {
                if (atoms[i] != null && atoms[i].type == "Person") uids.Add(atoms[i].uid);
            }
        } catch {}
        return uids;
    }

    private void CycleTargetAtom() {
        List<string> uids = GetPersonAtomUids();
        if (uids.Count == 0) { targetAtomUid = ""; return; }
        int idx = uids.IndexOf(targetAtomUid);
        idx = (idx + 1) % uids.Count;
        targetAtomUid = uids[idx];
        RefreshAtomDropdown();
    }

    private void RefreshAtomDropdown() {
        if (atomSelectorLabel == null) return;
        List<string> uids = GetPersonAtomUids();
        if (uids.Count == 0) {
            atomSelectorLabel.text = "无人物原子";
            targetAtomUid = "";
            return;
        }
        if (string.IsNullOrEmpty(targetAtomUid) || !uids.Contains(targetAtomUid)) targetAtomUid = uids[0];
        atomSelectorLabel.text = targetAtomUid + " (" + (uids.IndexOf(targetAtomUid) + 1) + "/" + uids.Count + ")";
    }

    private void UpdateAtomSelectorUI() {
        RefreshAtomDropdown();
    }

    private string DetectPresetTypeFromPath(string presetPathOrEntry) {
        string p = Norm(presetPathOrEntry).ToLowerInvariant();
        if (p.Contains("/animationpresets/")) return "Animation";
        if (p.Contains("/pose/")) return "Pose";
        if (p.Contains("/appearance/")) return "Appearance";
        if (p.Contains("/breastphysics/")) return "BreastPhysics";
        if (p.Contains("/clothing/")) return "Clothing";
        if (p.Contains("/hair/")) return "Hair";
        if (p.Contains("/morphs/")) return "Morphs";
        if (p.Contains("/pluginpresets/")) return "Plugins";
        if (p.Contains("/plugins/")) return "Plugins";
        if (p.Contains("/skin/")) return "Skin";
        if (p.Contains("/general/")) return "General";
        if (p.Contains("/full/")) return "Full";
        if (p.Contains("clothing")) return "Clothing";
        if (p.Contains("hair")) return "Hair";
        if (p.Contains("pose")) return "Pose";
        if (p.Contains("appearance") || p.Contains("look") || p.Contains("clothing")) return "Appearance";
        return "Full";
    }

    private string PresetDisplayNameFromPath(string presetPathOrEntry) {
        string n = Path.GetFileNameWithoutExtension(Norm(presetPathOrEntry));
        if (n.StartsWith("Preset_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
        return n;
    }

    private string GetPresetStorableId(string presetType) {
        if (presetType == "Appearance") return "AppearancePresets";
        if (presetType == "Pose") return "PosePresets";
        if (presetType == "General" || presetType == "Full") return "geometry";
        if (presetType == "Clothing") return "ClothingPresets";
        if (presetType == "Hair") return "HairPresets";
        if (presetType == "Morphs") return "MorphPresets";
        if (presetType == "Skin") return "SkinPresets";
        if (presetType == "Plugins") return "PluginPresets";
        if (presetType == "Animation") return "AnimationPresets";
        if (presetType == "BreastPhysics") return "FemaleBreastPhysicsPresets";
        if (presetType == "GlutePhysics") return "FemaleGlutePhysicsPresets";
        return "";
    }

    private Dictionary<string, bool> StorePresetLocks(Atom atom, bool lockClothing, bool lockHair) {
        Dictionary<string, bool> state = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (atom == null || atom.presetManagerControls == null) return state;
        for (int i = 0; i < atom.presetManagerControls.Count; i++) {
            var pmc = atom.presetManagerControls[i];
            if (pmc == null || string.IsNullOrEmpty(pmc.name)) continue;
            state[pmc.name] = pmc.lockParams;
            bool locked = (pmc.name == "ClothingPresets" && lockClothing) || (pmc.name == "HairPresets" && lockHair);
            SetPresetLockParam(pmc, locked);
        }
        return state;
    }

    private void RestorePresetLocks(Atom atom, Dictionary<string, bool> state) {
        if (atom == null || atom.presetManagerControls == null || state == null) return;
        for (int i = 0; i < atom.presetManagerControls.Count; i++) {
            var pmc = atom.presetManagerControls[i];
            if (pmc == null || string.IsNullOrEmpty(pmc.name)) continue;
            bool v;
            if (state.TryGetValue(pmc.name, out v)) SetPresetLockParam(pmc, v);
        }
    }

    private void SetPresetLockParam(MeshVR.PresetManagerControl pmc, bool value) {
        if (pmc == null) return;
        try { pmc.lockParams = value; } catch {}
        try {
            System.Reflection.MethodInfo sync = pmc.GetType().GetMethod("SyncLockParams", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (!object.ReferenceEquals(sync, null)) sync.Invoke(pmc, new object[] { value });
        } catch {}
    }

    private string SceneDisplayNameFromPath(string scenePath) {
        string n = Path.GetFileNameWithoutExtension(Norm(scenePath));
        return string.IsNullOrEmpty(n) ? Norm(scenePath) : n;
    }

    private void EnsureVarPresetIndex() {
        Stopwatch sw = Stopwatch.StartNew();
        varPresets.Clear();
        int packagesWithSpecs = 0;
        for (int i = 0; i < all.Count; i++) {
            PackageLite p = all[i];
            if (p == null || p.presetSpecs == null || p.presetSpecs.Count == 0) continue;
            packagesWithSpecs++;
            for (int j = 0; j < p.presetSpecs.Count; j++) {
                string spec = p.presetSpecs[j];
                string entryPath = PresetSpecPath(spec);
                var vp = new VarPresetLite();
                vp.package = p;
                vp.entryPath = entryPath;
                vp.presetType = PresetSpecType(spec);
                vp.name = PresetDisplayNameFromPath(entryPath);
                varPresets.Add(vp);
            }
        }
        varPresets.Sort((a, b) => {
            int c = PresetTypePriority(a.presetType).CompareTo(PresetTypePriority(b.presetType));
            if (c != 0) return c;
            c = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.package.uid, b.package.uid, StringComparison.OrdinalIgnoreCase);
        });
        sw.Stop();
        DebugLog("Var preset index built from cache. packages="+packagesWithSpecs+", presets="+varPresets.Count+", ms="+sw.Elapsed.TotalMilliseconds.ToString("0"));
    }

    private void EnsureSceneIndex() {
        sceneItems.Clear();
        for (int i = 0; i < all.Count; i++) {
            PackageLite p = all[i];
            if (p == null || p.scenes == null || p.scenes.Count == 0) continue;
            for (int j = 0; j < p.scenes.Count; j++) {
                string scene = p.scenes[j];
                if (string.IsNullOrEmpty(scene)) continue;
                SceneLite si = new SceneLite();
                si.package = p;
                si.entryPath = scene;
                si.name = SceneDisplayNameFromPath(scene);
                sceneItems.Add(si);
            }
        }
        sceneItems.Sort((a, b) => {
            int c = string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            return string.Compare(a.package.uid, b.package.uid, StringComparison.OrdinalIgnoreCase);
        });
    }

    private int PresetTypePriority(string t) {
        if (t == "Appearance") return 0;
        if (t == "Clothing") return 1;
        if (t == "Hair") return 2;
        if (t == "Skin") return 3;
        if (t == "General") return 4;
        if (t == "Full") return 5;
        if (t == "Morphs") return 6;
        if (t == "Plugins") return 7;
        if (t == "Animation") return 8;
        if (t == "BreastPhysics") return 9;
        if (t == "GlutePhysics") return 10;
        if (t == "Pose") return 11;
        return 99;
    }

    private bool PresetTypeMatchesActiveCat(string presetType) {
        if (activeCat == "Presets") return presetType == "Appearance";
        if (activeCat == "Clothing") return presetType == "Clothing";
        if (activeCat == "Hair") return presetType == "Hair";
        if (activeCat == "Morphs") return presetType == "Morphs";
        if (activeCat == "Scripts") return presetType == "Plugins";
        return false;
    }

    private void LoadPresetIntoAtom(Atom atom, string presetPathOrUrl, string presetType) {
        string t = string.IsNullOrEmpty(presetType) ? DetectPresetTypeFromPath(presetPathOrUrl) : presetType;
        string storableId = GetPresetStorableId(t);
        if (string.IsNullOrEmpty(storableId)) throw new Exception("不支持的预设类型：" + t);

        object control = FindPresetControl(atom, storableId, t);
        if (control == null) throw new Exception("找不到预设管理器：" + storableId);
        Dictionary<string, bool> lockState = null;
        bool needLock = (t == "Appearance");
        try {
            if (needLock) lockState = StorePresetLocks(atom, !applyClothing, !applyHair);
            InvokePresetLoadWithPath(control, presetPathOrUrl);
        } finally {
            if (needLock) RestorePresetLocks(atom, lockState);
        }
    }

    private void LoadPresetNameIntoAtom(Atom atom, string presetName, string presetType) {
        string t = string.IsNullOrEmpty(presetType) ? "Appearance" : presetType;
        string storableId = GetPresetStorableId(t);
        if (string.IsNullOrEmpty(storableId)) throw new Exception("不支持的预设类型：" + t);

        object control = FindPresetControl(atom, storableId, t);
        if (control == null) throw new Exception("找不到预设管理器：" + storableId);
        Dictionary<string, bool> lockState = null;
        bool needLock = (t == "Appearance");
        try {
            if (needLock) lockState = StorePresetLocks(atom, !applyClothing, !applyHair);
            InvokePresetLoadWithName(control, presetName);
        } finally {
            if (needLock) RestorePresetLocks(atom, lockState);
        }
    }

    private object FindPresetControl(Atom atom, string storableId, string presetType) {
        if (atom == null) return null;
        if ((presetType == "General" || presetType == "Full") && atom.mainPresetControl != null) return atom.mainPresetControl;
        try {
            if (atom.presetManagerControls != null) {
                for (int i = 0; i < atom.presetManagerControls.Count; i++) {
                    var pmc = atom.presetManagerControls[i];
                    if (pmc != null && string.Equals(pmc.name, storableId, StringComparison.OrdinalIgnoreCase)) return pmc;
                }
            }
        } catch {}
        try {
            JSONStorable js = atom.GetStorableByID(storableId);
            if (js != null) return js;
        } catch {}
        return null;
    }

    private void InvokePresetLoadWithPath(object control, string presetPathOrUrl) {
        if (control == null) throw new Exception("PresetManager 为空");
        string p = ToVamLoadPath(presetPathOrUrl);
        string beforeStatus = GetPresetControlStatus(control);
        DebugLog("InvokePresetLoadWithPath begin. control=" + control.GetType().Name + ", input=" + presetPathOrUrl + ", loadPath=" + p + ", statusBefore=" + beforeStatus);
        System.Reflection.MethodInfo m = control.GetType().GetMethod("LoadPresetWithPath", new Type[] { typeof(string) });
        if (!object.ReferenceEquals(m, null)) {
            try { m.Invoke(control, new object[] { p }); CheckPresetControlStatus(control, p); return; }
            catch(System.Reflection.TargetInvocationException tie) {
                if (!File.Exists(p) || SuperController.singleton == null) throw tie.InnerException == null ? tie : tie.InnerException;
                string np = SuperController.singleton.NormalizePath(p);
                m.Invoke(control, new object[] { np });
                CheckPresetControlStatus(control, np);
                return;
            }
        }
        JSONStorable js = control as JSONStorable;
        if (js == null) throw new Exception("PresetManager 不支持 LoadPresetWithPath：" + control.GetType().Name);
        JSONStorableString presetNameJSS = js.GetStringJSONParam("presetName");
        if (presetNameJSS == null) throw new Exception("PresetManager presetName 不可用：" + control.GetType().Name);
        string loadPath = p;
        if (SuperController.singleton != null && File.Exists(p)) loadPath = SuperController.singleton.NormalizePath(p);
        presetNameJSS.val = loadPath;
        js.CallAction("LoadPreset");
        CheckPresetControlStatus(control, loadPath);
    }

    private void InvokePresetLoadWithName(object control, string presetName) {
        if (control == null) throw new Exception("PresetManager 为空");
        string n = (presetName ?? "").Replace('\\','/').TrimStart('/');
        DebugLog("InvokePresetLoadWithName begin. control=" + control.GetType().Name + ", presetName=" + n + ", statusBefore=" + GetPresetControlStatus(control));
        System.Reflection.MethodInfo m = control.GetType().GetMethod("SyncLoadPresetWithName", new Type[] { typeof(string) });
        if (!object.ReferenceEquals(m, null)) {
            m.Invoke(control, new object[] { n });
            CheckPresetControlStatus(control, n);
            return;
        }
        JSONStorable js = control as JSONStorable;
        if (js == null) throw new Exception("PresetManager 不支持 SyncLoadPresetWithName：" + control.GetType().Name);
        JSONStorableString loadName = js.GetStringJSONParam("loadPresetWithName");
        if (loadName != null) {
            loadName.val = n;
            CheckPresetControlStatus(control, n);
            return;
        }
        JSONStorableString presetNameJSS = js.GetStringJSONParam("presetName");
        if (presetNameJSS == null) throw new Exception("PresetManager presetName 不可用：" + control.GetType().Name);
        presetNameJSS.val = n;
        js.CallAction("LoadPreset");
        CheckPresetControlStatus(control, n);
    }

    private string BuildVamPackagePresetName(Atom atom, string packageUid, string entryPath, string presetType) {
        string t = string.IsNullOrEmpty(presetType) ? DetectPresetTypeFromPath(entryPath) : presetType;
        string storableId = GetPresetStorableId(t);
        object control = FindPresetControl(atom, storableId, t);
        string storeFolder = GetPresetStoreFolderFromControl(control);
        if (string.IsNullOrEmpty(storeFolder)) storeFolder = GetPresetStoreFolderByType(t);
        string storeName = GetPresetStoreNameFromControl(control);
        if (string.IsNullOrEmpty(storeName)) storeName = "Preset";

        string rel = Norm(entryPath).TrimStart('/');
        string sf = Norm(storeFolder).TrimStart('/');
        if (!sf.EndsWith("/")) sf += "/";
        if (rel.StartsWith(sf, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(sf.Length);

        string dir = Norm(Path.GetDirectoryName(rel) ?? "").Trim('/');
        string file = Path.GetFileName(rel);
        string name = file;
        if (name.EndsWith(".vap", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
        string prefix = storeName + "_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(prefix.Length);

        string presetName = packageUid + ":";
        if (!string.IsNullOrEmpty(dir)) presetName += dir + "/";
        presetName += name;
        DebugLog("BuildVamPackagePresetName: uid=" + packageUid + ", entry=" + entryPath + ", type=" + t + ", storeFolder=" + storeFolder + ", storeName=" + storeName + " => " + presetName);
        return presetName;
    }

    private string BuildVamLocalPresetName(Atom atom, string presetPath, string presetType) {
        string t = string.IsNullOrEmpty(presetType) ? DetectPresetTypeFromPath(presetPath) : presetType;
        string storableId = GetPresetStorableId(t);
        object control = FindPresetControl(atom, storableId, t);
        string storeFolder = GetPresetStoreFolderFromControl(control);
        if (string.IsNullOrEmpty(storeFolder)) storeFolder = GetPresetStoreFolderByType(t);
        string storeName = GetPresetStoreNameFromControl(control);
        if (string.IsNullOrEmpty(storeName)) storeName = "Preset";

        string rel = presetPath ?? "";
        try {
            if (File.Exists(rel)) rel = MakeRel(vamRoot, Path.GetFullPath(rel));
        } catch {}
        rel = Norm(rel).TrimStart('/');

        string sf = Norm(storeFolder).TrimStart('/');
        if (!sf.EndsWith("/")) sf += "/";
        if (rel.StartsWith(sf, StringComparison.OrdinalIgnoreCase)) {
            rel = rel.Substring(sf.Length);
        } else {
            string fallbackStore = Norm(GetPresetStoreFolderByType(t)).TrimStart('/');
            if (!fallbackStore.EndsWith("/")) fallbackStore += "/";
            if (rel.StartsWith(fallbackStore, StringComparison.OrdinalIgnoreCase)) rel = rel.Substring(fallbackStore.Length);
        }

        string dir = Norm(Path.GetDirectoryName(rel) ?? "").Trim('/');
        string file = Path.GetFileName(rel);
        string name = file;
        if (name.EndsWith(".vap", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.EndsWith(".vaj", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 5);
        string prefix = storeName + "_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(prefix.Length);

        string presetName = string.IsNullOrEmpty(dir) ? name : (dir + "/" + name);
        DebugLog("BuildVamLocalPresetName: path=" + presetPath + ", rel=" + rel + ", type=" + t + ", storeFolder=" + storeFolder + ", storeName=" + storeName + " => " + presetName);
        return presetName;
    }

    private string GetPresetStoreFolderFromControl(object control) {
        try {
            object pm = GetPresetManagerFromControl(control);
            if (pm == null) return "";
            System.Reflection.MethodInfo m = pm.GetType().GetMethod("GetStoreFolderPath", new Type[] { typeof(bool) });
            if (!object.ReferenceEquals(m, null)) {
                object v = m.Invoke(pm, new object[] { false });
                return v == null ? "" : v.ToString();
            }
        } catch(Exception e) { DebugLog("GetPresetStoreFolderFromControl failed: " + e.Message); }
        return "";
    }

    private string GetPresetStoreNameFromControl(object control) {
        try {
            object pm = GetPresetManagerFromControl(control);
            if (pm == null) return "";
            System.Reflection.FieldInfo f = pm.GetType().GetField("storeName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (!object.ReferenceEquals(f, null)) {
                object v = f.GetValue(pm);
                return v == null ? "" : v.ToString();
            }
        } catch(Exception e) { DebugLog("GetPresetStoreNameFromControl failed: " + e.Message); }
        return "";
    }

    private object GetPresetManagerFromControl(object control) {
        if (control == null) return null;
        try {
            System.Reflection.FieldInfo f = control.GetType().GetField("pm", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (!object.ReferenceEquals(f, null)) return f.GetValue(control);
        } catch {}
        return null;
    }

    private string GetPresetStoreFolderByType(string presetType) {
        if (presetType == "Animation") return "Custom/Atom/Person/AnimationPresets/";
        if (presetType == "BreastPhysics") return "Custom/Atom/Person/BreastPhysics/";
        if (presetType == "Clothing") return "Custom/Atom/Person/Clothing/";
        if (presetType == "Hair") return "Custom/Atom/Person/Hair/";
        if (presetType == "Morphs") return "Custom/Atom/Person/Morphs/";
        if (presetType == "Plugins") return "Custom/PluginPresets/";
        if (presetType == "Pose") return "Custom/Atom/Person/Pose/";
        if (presetType == "Skin") return "Custom/Atom/Person/Skin/";
        if (presetType == "General") return "Custom/Atom/Person/General/";
        if (presetType == "Full") return "Saves/Person/Full/";
        return "Custom/Atom/Person/Appearance/";
    }

    private string ToVamLoadPath(string pathOrUrl) {
        string p = pathOrUrl ?? "";
        if (p.IndexOf(":/", StringComparison.Ordinal) >= 0) return Norm(p);
        try {
            if (File.Exists(p)) {
                string fullRoot = Path.GetFullPath(vamRoot).TrimEnd('\\','/') + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(p);
                if (full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) return Norm(MakeRel(vamRoot, full));
            }
        } catch {}
        return p.Replace('\\','/');
    }

    private string GetPresetControlStatus(object control) {
        try {
            System.Reflection.FieldInfo f = control.GetType().GetField("statusText", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (!object.ReferenceEquals(f, null)) {
                Text t = f.GetValue(control) as Text;
                if (t != null && !string.IsNullOrEmpty(t.text)) return t.text;
            }
        } catch {}
        return "";
    }

    private void CheckPresetControlStatus(object control, string loadPath) {
        string s = GetPresetControlStatus(control);
        DebugLog("InvokePresetLoadWithPath end. loadPath=" + loadPath + ", statusAfter=" + s);
        if (!string.IsNullOrEmpty(s) && s.IndexOf("Failed to load preset", StringComparison.OrdinalIgnoreCase) >= 0) {
            throw new Exception(s + " | " + loadPath);
        }
    }

    private void ApplyPresetToAtom(string presetPath) {
        try {
            List<string> uids = GetPersonAtomUids();
            if (uids.Count == 0) { SetStatus("场景中没有人物原子。", true); return; }
            if (string.IsNullOrEmpty(targetAtomUid) || !uids.Contains(targetAtomUid)) targetAtomUid = uids[0];
            Atom atom = SuperController.singleton.GetAtomByUid(targetAtomUid);
            if (atom == null) { SetStatus("找不到原子：" + targetAtomUid, true); return; }
            if (!File.Exists(presetPath)) { SetStatus("预设文件不存在：" + presetPath, true); return; }
            DebugLog("ApplyPresetToAtom begin. " + DescribeSelectionState() + ", requestedPath=" + presetPath);

            // Auto-link referenced packages
            string json = File.ReadAllText(presetPath, Encoding.UTF8);
            PresetLinkDiag diag = AutoLinkPresetDepsDetailed(json);
            bool depIssues = diag.missing.Count > 0 || diag.errors.Count > 0;
            if (depIssues) {
                DebugLog("ApplyPresetToAtom force-load despite dependency issues. missing=" + string.Join(",", diag.missing.ToArray()) + " errors=" + string.Join(";", diag.errors.ToArray()));
            }

            string presetType = selectedPreset != null && string.Equals(selectedPreset.fullPath, presetPath, StringComparison.OrdinalIgnoreCase)
                ? selectedPreset.presetType
                : DetectPresetTypeFromPath(presetPath);

            // VaM PresetManager wants a short presetName (e.g. "colo" or "Folder/colo"),
            // not "Custom/Atom/Person/Appearance/Preset_colo.vap"; otherwise it prepends
            // the store folder and "Preset_" again and tries to load a doubled path.
            string loadName = BuildVamLocalPresetName(atom, presetPath, presetType);
            try {
                LoadPresetNameIntoAtom(atom, loadName, presetType);
            } catch(Exception loadEx) {
                DebugLog("ApplyPresetToAtom name load failed, fallback to path load. name=" + loadName + ", path=" + presetPath + ", err=" + loadEx.Message);
                LoadPresetIntoAtom(atom, presetPath, presetType);
            }

            string msg = "已应用" + presetType + "预设到 " + targetAtomUid + "：" + Path.GetFileName(presetPath);
            if (diag.linked > 0) msg += "，自动链接=" + diag.linked + "包";
            if (depIssues) {
                msg += "；已强制加载";
                if (diag.missing.Count > 0) msg += "，缺失依赖=" + string.Join(",", diag.missing.ToArray());
                if (diag.errors.Count > 0) msg += "，依赖错误=" + string.Join("；", diag.errors.ToArray());
            }
            SetStatus(msg, true);
            DebugLog("ApplyPresetToAtom OK. atom=" + targetAtomUid + ", path=" + presetPath + ", loadName=" + loadName + ", type=" + presetType + ", linked=" + diag.linked + ", already=" + diag.already + ", forceDepIssues=" + depIssues);
        } catch (Exception e) {
            SetStatus("应用预设失败：" + e.Message, true);
            DebugLog("ApplyPresetToAtom FAILED: " + e.ToString());
        }
    }

    private void ApplySelectedPresetToAtom() {
        DebugLog("ApplySelectedPresetToAtom dispatch. " + DescribeSelectionState());
        if (selectedPreset != null) {
            DebugLog("ApplySelectedPresetToAtom branch=local-preset type=" + selectedPreset.presetType);
            ApplyPresetToAtom(selectedPreset.fullPath);
            return;
        }
        if (selectedVarPreset != null) {
            DebugLog("ApplySelectedPresetToAtom branch=package-preset-entry type=" + selectedVarPreset.presetType);
            ApplyPackagePresetEntryToAtom(selectedVarPreset);
            return;
        }
        if (selected != null) {
            // 按当前分类找对应类型预设，避免形态包误载 Appearance 人物外观
            string preferType = PreferredPresetTypeForActiveCat();
            DebugLog("ApplySelectedPresetToAtom branch=package-auto-find preferType=" + preferType);
            ApplyPackagePresetToAtom(selected, preferType);
            return;
        }
        SetStatus("请先选择一个预设。", false);
    }

    private string PreferredPresetTypeForActiveCat() {
        if (activeCat == "Morphs") return "Morphs";
        if (activeCat == "Clothing") return "Clothing";
        if (activeCat == "Hair") return "Hair";
        if (activeCat == "Presets") return "Appearance";
        if (activeCat == "Scripts") return "Plugins";
        return "Appearance";
    }

    private PresetLite selectedPreset;
    private VarPresetLite selectedVarPreset;
    private SceneLite selectedSceneItem;
    private WearableLite selectedWearableItem;

    private void ApplyPackagePresetToAtom(PackageLite p) {
        ApplyPackagePresetToAtom(p, PreferredPresetTypeForActiveCat());
    }

    private void ApplyPackagePresetToAtom(PackageLite p, string preferredType) {
        try {
            List<string> uids = GetPersonAtomUids();
            if (uids.Count == 0) { SetStatus("场景中没有人物原子。", true); return; }
            if (string.IsNullOrEmpty(targetAtomUid) || !uids.Contains(targetAtomUid)) targetAtomUid = uids[0];
            Atom atom = SuperController.singleton.GetAtomByUid(targetAtomUid);
            if (atom == null) { SetStatus("找不到原子：" + targetAtomUid, true); return; }
            if (string.IsNullOrEmpty(preferredType)) preferredType = "Appearance";
            DebugLog("ApplyPackagePresetToAtom begin. package=" + (p == null ? "-" : p.uid) + ", preferredType=" + preferredType + ", " + DescribeSelectionState());

            // Ensure the package itself is linked
            if (!IsAvailableInAddon(p.uid)) {
                LinkResult lr = LinkWithDeps(p);
                if (lr.created == 0 && lr.already == 0) { SetStatus("链接包失败：" + p.uid, true); return; }
                RefreshVam();
            }

            // 严格按期望类型查找；形态绝不回退到 Appearance，避免把对应人物外观一并套上
            string presetEntry = FindPresetInPackage(p, preferredType, preferredType != "Morphs" && preferredType != "Clothing" && preferredType != "Hair" && preferredType != "Plugins");
            if (string.IsNullOrEmpty(presetEntry)) {
                SetStatus("该包中没有找到 " + preferredType + " 预设。", true);
                return;
            }

            // Auto-link dependencies referenced in the preset JSON
            byte[] data = ReadBytes(p, presetEntry, 20L * 1024L * 1024L);
            PresetLinkDiag diag = new PresetLinkDiag();
            if (data != null && data.Length > 0) {
                string json = Encoding.UTF8.GetString(data);
                diag = AutoLinkPresetDepsDetailed(json);
                if (diag.missing.Count > 0 || diag.errors.Count > 0) {
                    DebugLog("ApplyPackagePresetToAtom force-load despite dependency issues. missing=" + string.Join(",", diag.missing.ToArray()) + " errors=" + string.Join(";", diag.errors.ToArray()));
                }
            }

            string presetType = DetectPresetTypeFromPath(presetEntry);
            // 形态/服装/头发：强制使用期望类型，防止路径误判
            if (!string.IsNullOrEmpty(preferredType) && preferredType != "Appearance") {
                if (!string.Equals(presetType, preferredType, StringComparison.OrdinalIgnoreCase)) {
                    DebugLog("ApplyPackagePresetToAtom coerce type " + presetType + " -> " + preferredType + " for entry=" + presetEntry);
                    presetType = preferredType;
                }
            }
            if (string.Equals(preferredType, "Morphs", StringComparison.OrdinalIgnoreCase) && !string.Equals(presetType, "Morphs", StringComparison.OrdinalIgnoreCase)) {
                SetStatus("已阻止：形态应用不会加载人物外观预设。entry=" + presetEntry, true);
                return;
            }

            string loadSource = BuildVamPackagePresetName(atom, p.uid, presetEntry, presetType);
            try {
                LoadPresetNameIntoAtom(atom, loadSource, presetType);
            } catch(Exception loadEx) {
                DebugLog("ApplyPackagePresetToAtom package-url load failed, fallback to local materialized preset. source=" + loadSource + ", err=" + loadEx.Message);
                loadSource = MaterializePackageEntryToTempFile(p, presetEntry);
                LoadPresetIntoAtom(atom, loadSource, presetType);
            }

            string msg = "已应用 " + p.uid + " 的" + presetType + "预设到 " + targetAtomUid + " [" + presetEntry + "]";
            if (diag.linked > 0) msg += "，自动链接=" + diag.linked + "包";
            if (diag.missing.Count > 0 || diag.errors.Count > 0) {
                msg += "；已强制加载";
                if (diag.missing.Count > 0) msg += "，缺失依赖=" + string.Join(",", diag.missing.ToArray());
                if (diag.errors.Count > 0) msg += "，依赖错误=" + string.Join("；", diag.errors.ToArray());
            }
            SetStatus(msg, true);
            DebugLog("ApplyPackagePresetToAtom OK: " + loadSource + " (src " + p.uid + ":/" + presetEntry + ") -> " + targetAtomUid + ", type=" + presetType + ", linked=" + diag.linked + ", already=" + diag.already + ", forceDepIssues=" + (diag.missing.Count > 0 || diag.errors.Count > 0));
        } catch (Exception e) {
            SetStatus("应用包预设失败：" + e.Message, true);
            DebugLog("ApplyPackagePresetToAtom FAILED: " + e.ToString());
        }
    }

    private void ApplyPackagePresetEntryToAtom(VarPresetLite vp) {
        if (vp == null || vp.package == null) { SetStatus("未选择包内预设。", true); return; }
        try {
            List<string> uids = GetPersonAtomUids();
            if (uids.Count == 0) { SetStatus("场景中没有人物原子。", true); return; }
            if (string.IsNullOrEmpty(targetAtomUid) || !uids.Contains(targetAtomUid)) targetAtomUid = uids[0];
            Atom atom = SuperController.singleton.GetAtomByUid(targetAtomUid);
            if (atom == null) { SetStatus("找不到原子：" + targetAtomUid, true); return; }
            DebugLog("ApplyPackagePresetEntryToAtom begin. source=" + vp.package.uid + ":/" + vp.entryPath + ", type=" + vp.presetType + ", " + DescribeSelectionState());

            if (!IsAvailableInAddon(vp.package.uid)) {
                LinkResult lr = LinkWithDeps(vp.package);
                if (lr.created == 0 && lr.already == 0) { SetStatus("链接包失败：" + vp.package.uid, true); return; }
                RefreshVam();
            }

            byte[] data = ReadBytes(vp.package, vp.entryPath, 20L * 1024L * 1024L);
            PresetLinkDiag diag = new PresetLinkDiag();
            if (data != null && data.Length > 0) {
                string json = Encoding.UTF8.GetString(data);
                diag = AutoLinkPresetDepsDetailed(json);
                if (diag.missing.Count > 0 || diag.errors.Count > 0) {
                    DebugLog("ApplyPackagePresetEntryToAtom force-load despite dependency issues. missing=" + string.Join(",", diag.missing.ToArray()) + " errors=" + string.Join(";", diag.errors.ToArray()));
                }
            }

            string loadSource = BuildVamPackagePresetName(atom, vp.package.uid, vp.entryPath, vp.presetType);
            try {
                LoadPresetNameIntoAtom(atom, loadSource, vp.presetType);
            } catch(Exception loadEx) {
                DebugLog("ApplyPackagePresetEntryToAtom package-url load failed, fallback to local materialized preset. source=" + loadSource + ", err=" + loadEx.Message);
                loadSource = MaterializePackageEntryToTempFile(vp.package, vp.entryPath);
                LoadPresetIntoAtom(atom, loadSource, vp.presetType);
            }
            string msg = "已应用 " + vp.package.uid + " 的" + vp.presetType + "预设到 " + targetAtomUid + " [" + vp.entryPath + "]";
            if (diag.linked > 0) msg += "，自动链接=" + diag.linked + "包";
            if (diag.missing.Count > 0 || diag.errors.Count > 0) {
                msg += "；已强制加载";
                if (diag.missing.Count > 0) msg += "，缺失依赖=" + string.Join(",", diag.missing.ToArray());
                if (diag.errors.Count > 0) msg += "，依赖错误=" + string.Join("；", diag.errors.ToArray());
            }
            SetStatus(msg, true);
            DebugLog("ApplyPackagePresetEntryToAtom OK: " + loadSource + " (src " + vp.package.uid + ":/" + vp.entryPath + ") -> " + targetAtomUid + ", type=" + vp.presetType + ", linked=" + diag.linked + ", already=" + diag.already + ", forceDepIssues=" + (diag.missing.Count > 0 || diag.errors.Count > 0));
        } catch (Exception e) {
            SetStatus("应用包内预设失败：" + e.Message, true);
            DebugLog("ApplyPackagePresetEntryToAtom FAILED: " + e.ToString());
        }
    }

    private string FindAppearancePresetInPackage(PackageLite p) {
        return FindPresetInPackage(p, "Appearance", true);
    }

    /// <summary>
    /// 在包内查找指定类型的人物预设。allowFallback 仅用于 Appearance 等通用场景；
    /// Morphs/Clothing/Hair 等应传 false，避免误落到人物外观。
    /// </summary>
    private string FindPresetInPackage(PackageLite p, string preferredType, bool allowFallback) {
        if (p == null) return "";
        string want = string.IsNullOrEmpty(preferredType) ? "Appearance" : preferredType;

        // 优先用索引中的 presetSpecs（类型可靠）
        if (p.presetSpecs != null && p.presetSpecs.Count > 0) {
            string best = "";
            for (int i = 0; i < p.presetSpecs.Count; i++) {
                string spec = p.presetSpecs[i];
                string t = PresetSpecType(spec);
                string path = PresetSpecPath(spec);
                if (!string.Equals(t, want, StringComparison.OrdinalIgnoreCase)) continue;
                string low = Norm(path).ToLowerInvariant();
                if (low.EndsWith(".vap") || low.EndsWith(".vaj")) return path;
                if (best == "") best = path;
            }
            if (best != "") return best;
            if (!allowFallback) return "";
        }

        ZipFile zip = null;
        try {
            zip = new ZipFile(p.fullPath);
            IEnumerator en = zip.GetEnumerator();
            string bestTyped = "", bestVap = "", bestJson = "", fallback = "";
            while (en.MoveNext()) {
                ZipEntry e = en.Current as ZipEntry;
                if (e == null || !e.IsFile) continue;
                string n = Norm(e.Name);
                string nl = n.ToLowerInvariant();
                if (!(nl.EndsWith(".vap") || nl.EndsWith(".vaj") || nl.EndsWith(".json"))) continue;
                if (!IsPersonPresetPath(n) && !nl.Contains("/appearance/") && !nl.Contains("/morphs/") && !nl.Contains("/clothing/") && !nl.Contains("/hair/")) continue;
                string t = DetectPresetTypeFromPath(n);
                if (string.Equals(t, want, StringComparison.OrdinalIgnoreCase)) {
                    if (nl.EndsWith(".vap") || nl.EndsWith(".vaj")) return n;
                    if (bestTyped == "") bestTyped = n;
                    continue;
                }
                if (!allowFallback) continue;
                if (nl.EndsWith(".vap") || nl.EndsWith(".vaj")) {
                    if (nl.Contains("appearance") || nl.Contains("clothing") || nl.Contains("look") || nl.Contains("person")) {
                        if (bestVap == "") bestVap = n;
                    } else if (bestVap == "") bestVap = n;
                } else if (nl.EndsWith(".json")) {
                    if (nl.StartsWith("custom/atom/person/appearance/") || nl.StartsWith("saves/person/appearance/") || nl.StartsWith("saves/person/full/")) {
                        if (bestJson == "") bestJson = n;
                    } else if (nl.Contains("appearance") && fallback == "") {
                        fallback = n;
                    }
                }
            }
            if (bestTyped != "") return bestTyped;
            if (!allowFallback) return "";
            if (bestVap != "") return bestVap;
            if (bestJson != "") return bestJson;
            return fallback;
        } catch { return ""; }
        finally { if (zip != null) zip.Close(); }
    }

    private void LoadScriptToAtom() {
        try {
            if (selected == null) { SetStatus("请先选择一个脚本包。", true); return; }
            if (!selected.cats.Contains("Scripts") && !selected.cats.Contains("Plugins")) { SetStatus("所选包不是脚本类型。", true); return; }

            List<string> uids = GetPersonAtomUids();
            if (uids.Count == 0) { SetStatus("场景中没有人物原子。", true); return; }
            if (string.IsNullOrEmpty(targetAtomUid) || !uids.Contains(targetAtomUid)) targetAtomUid = uids[0];
            Atom atom = SuperController.singleton.GetAtomByUid(targetAtomUid);
            if (atom == null) { SetStatus("找不到原子：" + targetAtomUid, true); return; }

            // Ensure package is linked
            if (!IsAvailableInAddon(selected.uid)) {
                LinkResult lr = LinkWithDeps(selected);
                if (lr.created == 0 && lr.already == 0) { SetStatus("链接包失败：" + selected.uid, true); return; }
                RefreshVam();
            }

            // Find script entry point
            string scriptEntry = FindScriptEntryInPackage(selected);
            if (string.IsNullOrEmpty(scriptEntry)) { SetStatus("该包中没有找到脚本入口(.cslist/.cs)。", true); return; }

            // Do not hand VaM a PackageUID:/Custom/Scripts/... URL here.
            // VaM's native package scanner can fail on many valid .var zips with
            // "CodePage 437 not supported", after which the package URL looks
            // nonexistent even though the file is readable. Extract the script tree
            // to Custom/Scripts/_AllPackagesLinkerTemp and load from a local path.
            string pluginUrl = MaterializePackageScriptEntryToLocal(selected, scriptEntry);
            RefreshVam();

            // Get PluginManager from atom
            JSONStorable pmStorable = atom.GetStorableByID("PluginManager");
            MVRPluginManager pluginMgr = pmStorable as MVRPluginManager;
            if (pluginMgr == null) { SetStatus("无法获取原子的 PluginManager。", true); return; }

            // Prefer replacing an existing stale PackageUID:/Custom/Scripts/... slot.
            // Otherwise VaM keeps the old failed slot in UI and reloads it later,
            // producing "Plugin file <PackageUID>:/... does not exist" forever.
            MVRPlugin newPlugin = FindAndPatchMatchingPackageScriptSlot(pluginMgr, selected.uid, scriptEntry, pluginUrl);
            if (newPlugin == null) {
                newPlugin = pluginMgr.CreatePlugin();
                if (newPlugin == null) { SetStatus("创建插件槽位失败。", true); return; }
                SetPluginUrlAndReload(pluginMgr, newPlugin, pluginUrl, "create-local-script-slot");
            }
            CancelInvoke("ForceAllowPendingPluginPackages");
            Invoke("ForceAllowPendingPluginPackages", 0.3f);
            Invoke("ForceAllowPendingPluginPackages", 1.0f);
            CancelInvoke("PatchLoadedPackageScriptPluginUrls");
            Invoke("PatchLoadedPackageScriptPluginUrls", 0.5f);
            Invoke("PatchLoadedPackageScriptPluginUrls", 1.5f);
            QueueTargetAtomPluginPanel(atom, newPlugin);

            SetStatus("已加载脚本 " + selected.uid + " 到 " + targetAtomUid + " [" + scriptEntry + "]", true);
            DebugLog("LoadScriptToAtom OK: " + selected.uid + ":/" + scriptEntry + " => " + pluginUrl + " -> " + targetAtomUid);
        } catch (Exception e) {
            SetStatus("加载脚本失败：" + e.Message, true);
            DebugLog("LoadScriptToAtom FAILED: " + e.ToString());
        }
    }

    private void QueueTargetAtomPluginPanel(Atom atom, MVRPlugin plugin) {
        if (!autoOpenTargetAtomPluginPanel || atom == null || plugin == null) return;
        pendingPluginPanelAtomUid = atom.uid ?? "";
        pendingPluginPanelSlotUid = plugin.uid ?? "";
        pendingPluginPanelRetryCount = 0;
        if (string.IsNullOrEmpty(pendingPluginPanelAtomUid) || string.IsNullOrEmpty(pendingPluginPanelSlotUid)) {
            DebugLog("QueueTargetAtomPluginPanel skipped: target atom or plugin slot has no uid.");
            return;
        }
        CancelInvoke("TryOpenTargetAtomPluginPanel");
        Invoke("TryOpenTargetAtomPluginPanel", 0.75f);
        DebugLog("Queued native target-atom plugin panel: atom=" + pendingPluginPanelAtomUid + ", slot=" + pendingPluginPanelSlotUid);
    }

    private void RetryTargetAtomPluginPanel(string reason) {
        if (pendingPluginPanelRetryCount >= MaxPendingPluginPanelRetries) {
            DebugLog("Target-atom plugin panel gave up after " + pendingPluginPanelRetryCount + " retries: " + reason);
            pendingPluginPanelAtomUid = "";
            pendingPluginPanelSlotUid = "";
            return;
        }
        pendingPluginPanelRetryCount++;
        DebugLog("Target-atom plugin panel waiting (" + pendingPluginPanelRetryCount + "/" + MaxPendingPluginPanelRetries + "): " + reason);
        Invoke("TryOpenTargetAtomPluginPanel", 0.5f);
    }

    private MVRPlugin FindPluginSlotByUid(MVRPluginManager pluginMgr, string uid) {
        if (pluginMgr == null || string.IsNullOrEmpty(uid)) return null;
        List<MVRPlugin> slots = GetPluginSlots(pluginMgr);
        for (int i = 0; i < slots.Count; i++) {
            if (slots[i] != null && string.Equals(slots[i].uid, uid, StringComparison.OrdinalIgnoreCase)) return slots[i];
        }
        return null;
    }

    private bool TryClickNativePluginsButton(Transform rootTransform) {
        if (rootTransform == null) return false;
        try {
            Button[] buttons = rootTransform.GetComponentsInChildren<Button>(true);
            for (int i = 0; i < buttons.Length; i++) {
                Button button = buttons[i];
                if (button == null || !button.interactable) continue;
                Text label = button.GetComponentInChildren<Text>(true);
                string text = label == null ? "" : (label.text ?? "").Trim();
                string objectName = button.gameObject.name ?? "";
                // Match only the tab itself.  Do not use a broad "contains Plugin"
                // check here: plugin management panels also have destructive buttons.
                bool isPluginsTab = string.Equals(text, "Plugins", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, "Plugin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(objectName, "Plugins", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(objectName, "Plugin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(objectName, "PluginsButton", StringComparison.OrdinalIgnoreCase);
                if (!isPluginsTab) continue;
                button.onClick.Invoke();
                DebugLog("Opened VaM native Plugins tab via button: " + (text.Length > 0 ? text : objectName));
                return true;
            }
        } catch (Exception e) { DebugLog("TryClickNativePluginsButton failed: " + e.Message); }
        return false;
    }

    private void SetTransformActiveUpTo(Transform child, Transform boundary) {
        if (child == null) return;
        Transform current = child;
        // Do not walk into the complete HUD hierarchy if the expected boundary
        // is not an ancestor.  The plugin list itself is still made visible.
        bool bounded = boundary != null && (current == boundary || current.IsChildOf(boundary));
        for (int depth = 0; current != null && depth < 32; depth++) {
            current.gameObject.SetActive(true);
            if (bounded && current == boundary) break;
            if (!bounded) break;
            current = current.parent;
        }
    }

    private bool ForceOpenNativePluginPanel(MVRPluginManager pluginMgr, MVRPlugin plugin) {
        if (pluginMgr == null) return false;
        try {
            // InitUI is VaM's own wiring step.  It attaches configUI and every
            // script customUI to MVRPluginManagerUI.pluginListPanel/scriptUIParent.
            pluginMgr.InitUI();
            Transform listPanel = pluginMgr.pluginListPanel;
            if (listPanel == null) {
                DebugLog("Native plugin panel unavailable: MVRPluginManager.InitUI left pluginListPanel null.");
                return false;
            }
            SetTransformActiveUpTo(listPanel, pluginMgr.UITransform);
            if (pluginMgr.scriptUIParent != null) SetTransformActiveUpTo(pluginMgr.scriptUIParent, pluginMgr.UITransform);
            if (plugin != null && plugin.configUI != null) SetTransformActiveUpTo(plugin.configUI, listPanel);
            DebugLog("Native plugin panel forced visible: panel=" + listPanel.name + ", active=" + listPanel.gameObject.activeInHierarchy + ", pluginUi=" + (plugin == null || plugin.configUI == null ? "-" : plugin.configUI.gameObject.activeInHierarchy.ToString()));
            return listPanel.gameObject.activeInHierarchy;
        } catch (Exception e) {
            DebugLog("ForceOpenNativePluginPanel failed: " + e.ToString());
            return false;
        }
    }

    private void TryOpenTargetAtomPluginPanel() {
        if (!autoOpenTargetAtomPluginPanel || string.IsNullOrEmpty(pendingPluginPanelAtomUid) || string.IsNullOrEmpty(pendingPluginPanelSlotUid)) return;
        try {
            SuperController sc = SuperController.singleton;
            if (sc == null) { RetryTargetAtomPluginPanel("SuperController not ready"); return; }
            Atom atom = sc.GetAtomByUid(pendingPluginPanelAtomUid);
            if (atom == null) { RetryTargetAtomPluginPanel("target atom not found: " + pendingPluginPanelAtomUid); return; }
            MVRPluginManager pluginMgr = atom.GetStorableByID("PluginManager") as MVRPluginManager;
            if (pluginMgr == null) { RetryTargetAtomPluginPanel("target atom has no PluginManager"); return; }
            MVRPlugin plugin = FindPluginSlotByUid(pluginMgr, pendingPluginPanelSlotUid);
            if (plugin == null) { RetryTargetAtomPluginPanel("plugin slot not created yet"); return; }
            if (plugin.scriptControllers == null || plugin.scriptControllers.Count == 0) {
                RetryTargetAtomPluginPanel("script is still loading");
                return;
            }

            // The custom library canvas otherwise sits in front of VaM's HUD.
            if (canvas != null) ClosePanel();
            if (atom.mainController == null) { RetryTargetAtomPluginPanel("target atom main controller not ready"); return; }
            sc.SelectController(atom.mainController, false, false, false, true);
            sc.ShowMainHUDAuto();

            bool nativePanelOpened = ForceOpenNativePluginPanel(pluginMgr, plugin);
            // Older VaM UI skins can expose a real Plugins tab instead; retain a
            // narrow, non-destructive fallback for those layouts.
            if (!nativePanelOpened) nativePanelOpened = TryClickNativePluginsButton(atom.UITransform);
            if (!nativePanelOpened) nativePanelOpened = TryClickNativePluginsButton(atom.UITransformAlt);
            if (!nativePanelOpened) nativePanelOpened = TryClickNativePluginsButton(pluginMgr.UITransform);
            if (!nativePanelOpened) nativePanelOpened = TryClickNativePluginsButton(pluginMgr.UITransformAlt);

            int openedScriptUis = 0;
            for (int i = 0; i < plugin.scriptControllers.Count; i++) {
                MVRScriptController controller = plugin.scriptControllers[i];
                if (controller == null) continue;
                try { controller.OpenUI(); openedScriptUis++; }
                catch (Exception e) { DebugLog("Open script UI failed: " + e.Message); }
            }
            DebugLog("Opened target atom native UI. atom=" + atom.uid + ", slot=" + plugin.uid + ", nativePluginPanel=" + nativePanelOpened + ", scriptUIs=" + openedScriptUis);
            pendingPluginPanelAtomUid = "";
            pendingPluginPanelSlotUid = "";
        } catch (Exception e) {
            DebugLog("TryOpenTargetAtomPluginPanel FAILED: " + e.ToString());
            RetryTargetAtomPluginPanel(e.Message);
        }
    }

    private List<MVRPlugin> GetPluginSlots(MVRPluginManager pluginMgr) {
        List<MVRPlugin> result = new List<MVRPlugin>();
        try {
            if (pluginMgr == null) return result;
            System.Reflection.FieldInfo f = pluginMgr.GetType().GetField("plugins", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (object.ReferenceEquals(f, null)) return result;
            IList list = f.GetValue(pluginMgr) as IList;
            if (list == null) return result;
            for (int i = 0; i < list.Count; i++) {
                MVRPlugin p = list[i] as MVRPlugin;
                if (p != null) result.Add(p);
            }
        } catch (Exception e) {
            DebugLog("GetPluginSlots failed: " + e.Message);
        }
        return result;
    }

    private string GetPluginUrl(MVRPlugin plugin) {
        try {
            if (plugin == null || plugin.pluginURLJSON == null) return "";
            return plugin.pluginURLJSON.val ?? "";
        } catch { return ""; }
    }

    private bool SamePluginUrl(string a, string b) {
        return string.Equals(Norm(a).TrimStart('/'), Norm(b).TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }

    private bool TryParsePackageScriptUrl(string url, out string uid, out string entry) {
        uid = "";
        entry = "";
        string u = Norm(url).Trim();
        if (u.Length == 0) return false;
        int sep = u.IndexOf(":/", StringComparison.Ordinal);
        int skip = 2;
        if (sep < 0) { sep = u.IndexOf(':'); skip = 1; }
        if (sep <= 0 || sep + skip >= u.Length) return false;
        uid = u.Substring(0, sep).Trim();
        entry = u.Substring(sep + skip).Trim().TrimStart('/');
        if (uid.Length == 0 || entry.Length == 0) return false;
        return entry.StartsWith("Custom/Scripts/", StringComparison.OrdinalIgnoreCase);
    }

    private string ScriptEntryKey(string entry) {
        string e = Norm(entry).Trim().TrimStart('/');
        if (e.StartsWith("Custom/Scripts/", StringComparison.OrdinalIgnoreCase)) e = e.Substring("Custom/Scripts/".Length);
        return e.ToLowerInvariant();
    }

    private bool PackageScriptUrlMatches(string url, string uid, string scriptEntry) {
        string oldUid, oldEntry;
        if (!TryParsePackageScriptUrl(url, out oldUid, out oldEntry)) return false;
        if (!string.Equals(Group(oldUid), Group(uid), StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(ScriptEntryKey(oldEntry), ScriptEntryKey(scriptEntry), StringComparison.OrdinalIgnoreCase);
    }

    private void SetPluginUrlAndReload(MVRPluginManager pluginMgr, MVRPlugin plugin, string pluginUrl, string reason) {
        if (plugin == null) return;
        string old = GetPluginUrl(plugin);
        try {
            if (plugin.pluginURLJSON != null) plugin.pluginURLJSON.val = pluginUrl;
            try {
                System.Reflection.MethodInfo sm = pluginMgr == null ? null : pluginMgr.GetType().GetMethod("SyncPluginUrl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (!object.ReferenceEquals(sm, null)) sm.Invoke(pluginMgr, new object[] { plugin });
            } catch(Exception e) { DebugLog("SyncPluginUrl failed: " + e.Message); }
            try { plugin.Reload(); } catch(Exception e) { DebugLog("Plugin Reload failed: " + e.Message); }
            DebugLog("SetPluginUrlAndReload OK: uid=" + (plugin.uid ?? "") + ", old=" + old + ", new=" + pluginUrl + ", reason=" + reason);
        } catch (Exception e) {
            DebugLog("SetPluginUrlAndReload FAILED: old=" + old + ", new=" + pluginUrl + ", reason=" + reason + ", err=" + e.ToString());
        }
    }

    private MVRPlugin FindAndPatchMatchingPackageScriptSlot(MVRPluginManager pluginMgr, string uid, string scriptEntry, string pluginUrl) {
        MVRPlugin firstPatched = null;
        MVRPlugin exactLocal = null;
        int patched = 0;
        List<MVRPlugin> plugins = GetPluginSlots(pluginMgr);
        for (int i = 0; i < plugins.Count; i++) {
            MVRPlugin p = plugins[i];
            string url = GetPluginUrl(p);
            if (PackageScriptUrlMatches(url, uid, scriptEntry)) {
                SetPluginUrlAndReload(pluginMgr, p, pluginUrl, "replace-stale-package-script-url");
                patched++;
                if (firstPatched == null) firstPatched = p;
            } else if (SamePluginUrl(url, pluginUrl) && exactLocal == null) {
                exactLocal = p;
            }
        }
        if (firstPatched != null) {
            DebugLog("FindAndPatchMatchingPackageScriptSlot replaced stale package slots: uid=" + uid + ", entry=" + scriptEntry + ", patched=" + patched + ", local=" + pluginUrl);
            return firstPatched;
        }
        if (exactLocal != null) {
            SetPluginUrlAndReload(pluginMgr, exactLocal, pluginUrl, "reload-existing-local-script-slot");
            return exactLocal;
        }
        return null;
    }

    private void PatchLoadedPackageScriptPluginUrls() {
        int atomCount = 0, slotCount = 0, packageRefs = 0, patched = 0, errors = 0;
        try {
            if (SuperController.singleton == null) return;
            List<Atom> atoms = SuperController.singleton.GetAtoms();
            if (atoms == null) return;
            for (int ai = 0; ai < atoms.Count; ai++) {
                Atom atom = atoms[ai];
                if (atom == null) continue;
                atomCount++;
                JSONStorable pmStorable = null;
                try { pmStorable = atom.GetStorableByID("PluginManager"); } catch {}
                MVRPluginManager pluginMgr = pmStorable as MVRPluginManager;
                if (pluginMgr == null) continue;
                List<MVRPlugin> plugins = GetPluginSlots(pluginMgr);
                for (int pi = 0; pi < plugins.Count; pi++) {
                    slotCount++;
                    MVRPlugin plugin = plugins[pi];
                    string url = GetPluginUrl(plugin);
                    string uid, entry;
                    if (!TryParsePackageScriptUrl(url, out uid, out entry)) continue;
                    packageRefs++;
                    try {
                        PackageLite p;
                        string source;
                        if (!TryResolvePackageByUid(uid, out p, out source) || p == null) {
                            DebugLog("PatchLoadedPackageScriptPluginUrls unresolved: atom=" + atom.uid + ", plugin=" + (plugin.uid ?? "") + ", url=" + url);
                            continue;
                        }
                        string local = MaterializePackageScriptEntryToLocal(p, entry);
                        if (!SamePluginUrl(url, local)) {
                            SetPluginUrlAndReload(pluginMgr, plugin, local, "runtime-replace-package-script-url:" + source + ":atom=" + atom.uid);
                            patched++;
                        }
                    } catch (Exception e) {
                        errors++;
                        DebugLog("PatchLoadedPackageScriptPluginUrls slot failed: atom=" + atom.uid + ", plugin=" + (plugin == null ? "" : (plugin.uid ?? "")) + ", url=" + url + ", err=" + e.Message);
                    }
                }
            }
            DebugLog("PatchLoadedPackageScriptPluginUrls end. atoms=" + atomCount + ", slots=" + slotCount + ", packageRefs=" + packageRefs + ", patched=" + patched + ", errors=" + errors);
        } catch (Exception e) {
            DebugLog("PatchLoadedPackageScriptPluginUrls FAILED: " + e.ToString());
        }
    }

    private string FindScriptEntryInPackage(PackageLite p) {
        ZipFile zip = null;
        try {
            zip = new ZipFile(p.fullPath);
            IEnumerator en = zip.GetEnumerator();
            string bestCslist = "", bestCs = "";
            while (en.MoveNext()) {
                ZipEntry e = en.Current as ZipEntry;
                if (e == null || !e.IsFile) continue;
                string n = Norm(e.Name);
                string nl = n.ToLowerInvariant();
                if (nl.EndsWith(".cslist")) {
                    // Prefer .cslist files at the top level of Custom/Scripts/
                    if (bestCslist == "" || nl.Split('/').Length < bestCslist.Split('/').Length) bestCslist = n;
                } else if (nl.EndsWith(".cs") && bestCslist == "") {
                    if (nl.StartsWith("custom/scripts/") && (bestCs == "" || nl.Split('/').Length < bestCs.Split('/').Length)) bestCs = n;
                }
            }
            return bestCslist != "" ? bestCslist : bestCs;
        } catch { return ""; }
        finally { if (zip != null) zip.Close(); }
    }

    private PresetLinkDiag AutoLinkPresetDepsDetailed(string presetJson) {
        // Find all package references in the JSON (pattern: "Creator.PackageName.Version:/path")
        HashSet<string> needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        while (idx < presetJson.Length) {
            int colon = presetJson.IndexOf(":/", idx);
            if (colon < 0) break;
            // Walk backwards to find the package uid (format: word.word.number)
            int start = colon - 1;
            while (start >= 0 && presetJson[start] != '"' && presetJson[start] != ' ' && presetJson[start] != ',') start--;
            start++;
            if (start < colon) {
                string candidate = presetJson.Substring(start, colon - start).Trim();
                // Validate it looks like a package ref (has at least 2 dots)
                int dots = 0; for (int i = 0; i < candidate.Length; i++) if (candidate[i] == '.') dots++;
                if (dots >= 2 && candidate.Length > 5 && !candidate.Contains(" ") && !candidate.Contains("/")) {
                    // Normalize: could be "Creator.Package.Version" or "Creator.Package.latest"
                    needed.Add(candidate);
                }
            }
            idx = colon + 2;
        }
        PresetLinkDiag diag = new PresetLinkDiag();
        if (needed.Count == 0) return diag;

        Directory.CreateDirectory(linkRoot);
        foreach (string dep in needed) {
            if (IsAvailableInAddon(dep)) continue;
            PackageLite p = null;
            bool already = false;
            if (ResolveDep(dep, out p, out already)) {
                if (already) {
                    diag.already++;
                } else {
                    try {
                        LinkResult lr = LinkWithDeps(p);
                        diag.linked += lr.created;
                        diag.already += lr.already;
                        for (int i = 0; i < lr.missing.Count; i++) if (!diag.missing.Contains(lr.missing[i])) diag.missing.Add(lr.missing[i]);
                        for (int i = 0; i < lr.errors.Count; i++) diag.errors.Add(lr.errors[i]);
                    } catch(Exception e) {
                        diag.errors.Add(dep + ":" + e.Message);
                    }
                }
            } else {
                if (!diag.missing.Contains(dep)) diag.missing.Add(dep);
            }
        }
        if (diag.linked > 0 || diag.already > 0) {
            RefreshVam();
            DebugLog("AutoLinkPresetDeps recursive linked=" + diag.linked + ", already=" + diag.already + ", directRefs=" + needed.Count + ", missing=" + diag.missing.Count + ", errors=" + diag.errors.Count);
        }
        if (diag.missing.Count > 0 || diag.errors.Count > 0) {
            DebugLog("AutoLinkPresetDeps issues. missing=" + string.Join(",", diag.missing.ToArray()) + " errors=" + string.Join(";", diag.errors.ToArray()));
        }
        return diag;
    }

    private PresetLinkDiag AutoLinkSceneDepsDetailed(string sceneJson) {
        HashSet<string> needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        while (idx < sceneJson.Length) {
            int colon = sceneJson.IndexOf(":/", idx);
            if (colon < 0) break;
            int start = colon - 1;
            while (start >= 0) {
                char ch = sceneJson[start];
                if (ch == '"' || ch == ' ' || ch == ',' || ch == '\r' || ch == '\n' || ch == '\t' || ch == '[' || ch == ']' || ch == '{' || ch == '}') break;
                start--;
            }
            start++;
            if (start < colon) {
                string candidate = sceneJson.Substring(start, colon - start).Trim();
                int dots = 0; for (int i = 0; i < candidate.Length; i++) if (candidate[i] == '.') dots++;
                if (dots >= 2 && candidate.Length > 5 && !candidate.Contains(" ") && !candidate.Contains("/")) needed.Add(candidate);
            }
            idx = colon + 2;
        }
        PresetLinkDiag diag = new PresetLinkDiag();
        if (needed.Count == 0) {
            DebugLog("AutoLinkSceneDeps no direct package refs found in scene json.");
            return diag;
        }
        DebugLog("AutoLinkSceneDeps refs=" + needed.Count + " [" + string.Join(",", new List<string>(needed).ToArray()) + "]");

        Directory.CreateDirectory(linkRoot);
        foreach (string dep in needed) {
            PackageLite addonPkg = null;
            string addonSource = "";
            if (TryGetAvailableAddonPackage(dep, out addonPkg, out addonSource)) {
                diag.already++;
                DebugLog("AutoLinkSceneDeps already available: " + dep + " via " + addonSource + " -> " + addonPkg.fullPath);
                continue;
            }
            PackageLite p = null;
            bool already = false;
            string resolveSource = "";
            if (TryResolveDepDetailed(dep, out p, out already, out resolveSource)) {
                if (already) {
                    diag.already++;
                    DebugLog("AutoLinkSceneDeps resolved-as-already: " + dep + " via " + resolveSource + " -> " + (p == null ? "null" : p.fullPath));
                } else {
                    try {
                        DebugLog("AutoLinkSceneDeps linking: " + dep + " via " + resolveSource + " -> " + (p == null ? "null" : p.fullPath));
                        LinkResult lr = LinkWithDeps(p);
                        diag.linked += lr.created;
                        diag.already += lr.already;
                        for (int i = 0; i < lr.missing.Count; i++) if (!diag.missing.Contains(lr.missing[i])) diag.missing.Add(lr.missing[i]);
                        for (int i = 0; i < lr.errors.Count; i++) diag.errors.Add(lr.errors[i]);
                        DebugLog("AutoLinkSceneDeps link result: root=" + (p == null ? dep : p.uid) + ", created=" + lr.created + ", already=" + lr.already + ", missing=" + lr.missing.Count + ", errors=" + lr.errors.Count);
                    } catch(Exception e) {
                        diag.errors.Add(dep + ":" + e.Message);
                        DebugLog("AutoLinkSceneDeps link failed: " + dep + " -> " + e.Message);
                    }
                }
            } else {
                if (!diag.missing.Contains(dep)) diag.missing.Add(dep);
                DebugLog("AutoLinkSceneDeps missing: " + dep);
            }
        }
        DebugLog("AutoLinkSceneDeps recursive linked=" + diag.linked + ", already=" + diag.already + ", directRefs=" + needed.Count + ", missing=" + diag.missing.Count + ", errors=" + diag.errors.Count);
        if (diag.missing.Count > 0 || diag.errors.Count > 0) {
            DebugLog("AutoLinkSceneDeps issues. missing=" + string.Join(",", diag.missing.ToArray()) + " errors=" + string.Join(";", diag.errors.ToArray()));
        }
        return diag;
    }

    private int AutoLinkPresetDeps(string presetJson) {
        return AutoLinkPresetDepsDetailed(presetJson).linked;
    }

    private bool IsAvailableInAddon(string uid) {
        PackageLite p;
        string source;
        return TryGetAvailableAddonPackage(uid, out p, out source);
    }

    private bool IsClothingStorable(string id, JSONClass sj) {
        string idLow = id.ToLowerInvariant();
        if (idLow.Contains("clothing")) return true;
        // Check if the storable JSON references clothing paths
        string raw = sj.ToString();
        if (raw.Contains("/Clothing/") || raw.Contains("/clothing/")) return true;
        return false;
    }

    private bool IsHairStorable(string id, JSONClass sj) {
        string idLow = id.ToLowerInvariant();
        if (idLow.Contains("hair")) return true;
        string raw = sj.ToString();
        if (raw.Contains("/Hair/") || raw.Contains("/hair/")) return true;
        return false;
    }

    private string CatTextLabel(PackageLite p){ if(p==null||p.cats==null||p.cats.Count==0)return "其他"; List<string> labels=new List<string>(); for(int i=0;i<p.cats.Count;i++)labels.Add(CatLabel(p.cats[i])); return string.Join("，",labels.ToArray()); }
    private void LeaveSceneSelection(){
        if(selectedSceneAnalysis==null && selectedSceneItem==null)return;
        StopScenePrewarm(true);
        selectedSceneAnalysis=null;
    }
    private void SelectPackage(PackageLite p){
        LeaveSceneSelection();
        selected=p; selectedPreset=null; selectedVarPreset=null; selectedSceneItem=null; selectedWearableItem=null;
        LoadPreview(p);
        if(details!=null){
            // 分行展示，避免一行过长；详情区会裁剪，不再叠到按钮上
            StringBuilder sb = new StringBuilder();
            sb.Append(p.uid).Append('\n');
            sb.Append("分类：").Append(CatTextLabel(p)).Append('\n');
            sb.Append("路径：").Append(OneLine(p.relPath, 72)).Append('\n');
            sb.Append("依赖：").Append(p.deps.Count)
              .Append("  场景：").Append(p.scenes.Count)
              .Append("  预设：").Append(p.presetSpecs==null?0:p.presetSpecs.Count).Append('\n');
            if (p.firstScene != "") sb.Append("首场景：").Append(OneLine(p.firstScene, 56)).Append('\n');
            sb.Append("收藏：").Append(IsFavorite(p)?"是":"否")
              .Append("  默认保留：").Append(IsDefault(p)?"是":"否");
            if (!string.IsNullOrEmpty(p.description)) sb.Append('\n').Append(OneLine(p.description, 120));
            details.text = sb.ToString();
            details.color = colTextSecondary;
        }
        UpdateAtomSelectorUI();
        UpdateInspectorVisibility();
        SetStatus("已选择 "+p.uid, false);
    }
    private void LoadPreview(PackageLite p){ ClearPreview(); if(preview==null||p==null)return; try{ Texture2D tex; Sprite sp; if(!TryLoadPackageSprite(p,12L*1024L*1024L,out tex,out sp)) return; previewTex=tex; previewSprite=sp; preview.sprite=previewSprite; preview.color=Color.white;}catch(Exception e){Logger.LogWarning(e.Message);ClearPreview();}}
    private bool TryLoadPackageSprite(PackageLite p,long maxBytes,out Texture2D tex,out Sprite sp){
        tex=null; sp=null;
        try{
            byte[] bytes=null;
            if(p!=null && p.thumbCache!="" && File.Exists(p.thumbCache)){
                FileInfo cachedThumb=new FileInfo(p.thumbCache);
                if(cachedThumb.Length>0 && cachedThumb.Length<=maxBytes)bytes=File.ReadAllBytes(p.thumbCache);
            }
            if(bytes==null && p!=null && p.thumbEntry!="")bytes=ReadBytes(p,p.thumbEntry,maxBytes);
            if(bytes==null||bytes.Length==0)return false;
            tex=new Texture2D(2,2,TextureFormat.RGBA32,false);
            if(!tex.LoadImage(bytes)){Destroy(tex);tex=null;return false;}
            sp=Sprite.Create(tex,new Rect(0,0,tex.width,tex.height),new Vector2(0.5f,0.5f)); return true;
        }catch(Exception e){Logger.LogWarning("TryLoadPackageSprite failed: "+e.Message); if(sp!=null)Destroy(sp); if(tex!=null)Destroy(tex); tex=null; sp=null; return false;}
    }
    private void StopThumbLoadCoroutine(){ if(thumbLoadCoroutine!=null){ try{ StopCoroutine(thumbLoadCoroutine); }catch{} thumbLoadCoroutine=null; } }
    private void ClearPreview(){ if(preview!=null){preview.sprite=null;preview.color=colThumbBg;} if(previewSprite!=null)Destroy(previewSprite); if(previewTex!=null)Destroy(previewTex); previewSprite=null;previewTex=null; }
    private void ClearListThumbs(){ for(int i=0;i<listThumbSprites.Count;i++) if(listThumbSprites[i]!=null)Destroy(listThumbSprites[i]); for(int i=0;i<listThumbTextures.Count;i++) if(listThumbTextures[i]!=null)Destroy(listThumbTextures[i]); listThumbSprites.Clear(); listThumbTextures.Clear(); }
    private bool HasFavoriteSceneForPackage(PackageLite p){ if(p==null)return false; string prefix=p.uid+":/"; foreach(string s in favoriteScenes){ if(!string.IsNullOrEmpty(s) && s.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) return true; } return false; }
    private bool IsFavorite(PackageLite p){ return p!=null && (favoriteUids.Contains(p.uid) || (p.firstScene!="" && favoriteScenes.Contains(SceneRef(p,p.firstScene))) || HasFavoriteSceneForPackage(p)); }
    private bool IsDefault(PackageLite p){ return p!=null && defaultUids.Contains(p.uid); }
    private void ToggleFavoriteSelected(){ if(selectedSceneItem!=null){ ToggleSceneFavoriteItem(selectedSceneItem); return; } if(selectedPreset!=null){ TogglePresetFavorite(selectedPreset); return; } if(selectedVarPreset!=null && selectedVarPreset.package!=null){ ToggleFavorite(selectedVarPreset.package); return; } if(selectedWearableItem!=null && selectedWearableItem.package!=null){ ToggleFavorite(selectedWearableItem.package); return; } if(selected!=null) ToggleFavorite(selected); }
    private void ToggleDefaultSelected(){ if(selected!=null) ToggleDefault(selected); }
    private void ToggleFavorite(PackageLite p){
        if(p==null)return;
        if(favoriteUids.Contains(p.uid)){favoriteUids.Remove(p.uid); if(p.firstScene!="") favoriteScenes.Remove(SceneRef(p,p.firstScene)); SetStatus("已取消收藏："+p.uid,true);}
        else {favoriteUids.Add(p.uid); if(p.firstScene!="") favoriteScenes.Add(SceneRef(p,p.firstScene)); SetStatus("已加入收藏："+p.uid,true);}
        SaveMarks();
        // 刷新后恢复更具体的选择（形态/预设/场景），禁止退化为“选中包”导致误载 Appearance
        ReselectAfterMarkChange(p);
    }
    private void ToggleDefault(PackageLite p){
        if(p==null)return;
        if(defaultUids.Contains(p.uid)){defaultUids.Remove(p.uid);SetStatus("默认保留已关闭："+p.uid,true);}
        else {defaultUids.Add(p.uid);SetStatus("默认保留已开启："+p.uid,true);}
        SaveMarks();
        ReselectAfterMarkChange(p);
    }
    private void ReselectAfterMarkChange(PackageLite p) {
        VarPresetLite keepVp = selectedVarPreset;
        PresetLite keepPr = selectedPreset;
        SceneLite keepSc = selectedSceneItem;
        WearableLite keepW = selectedWearableItem;
        RefreshList();
        try {
            if (keepVp != null && keepVp.package != null) { SelectVarPreset(keepVp); return; }
            if (keepPr != null) { SelectPreset(keepPr); return; }
            if (keepSc != null) { SelectSceneItem(keepSc); return; }
            if (keepW != null) { SelectWearable(keepW); return; }
            if (p != null) SelectPackage(p);
        } catch (Exception e) { DebugLog("ReselectAfterMarkChange failed: " + e.Message); }
    }

    private void LinkSelected(bool load){ if(selected==null){SetStatus("未选择包。",false);return;} if(load){ if(selected.firstScene==""){SetStatus("选中的包没有场景："+selected.uid,true);return;} LoadPackageScene(selected,selected.firstScene); return; } LinkResult result=LinkWithDeps(selected); RefreshVam(); RefreshList(); string msg="已链接 "+selected.uid+"：新建="+result.created+"，已存在="+result.already+"，缺失依赖="+result.missing.Count+"，错误="+result.errors.Count; if(result.missing.Count>0) msg+=" | 缺失："+string.Join(", ",result.missing.ToArray()); if(result.errors.Count>0) msg+=" | 错误："+string.Join("；",result.errors.ToArray()); SetStatus(msg, true); }
    private void LoadPackageScene(PackageLite p,string scene){
        Stopwatch totalSw = Stopwatch.StartNew();
        try{
            if(Time.realtimeSinceStartup-lastLoadClickAt<0.75f){DebugLog("LoadPackageScene ignored duplicate click.");return;}
            lastLoadClickAt=Time.realtimeSinceStartup;
            if(p==null||scene==""){SetStatus("没有可加载的场景。",true);return;}
            CancelPendingSceneLoad();
            lazyCuaCandidateAtomUids.Clear();
            string requestedSceneKey = SceneAnalysisKey(p, scene);
            DebugLog("LoadPackageScene begin. uid="+p.uid+", scene="+scene+", mode="+SceneLoadModeName(sceneLoadMode)+", primary="+scenePrimaryPersonId);
            selected=p;
            if (selectedSceneItem == null || selectedSceneItem.package != p || !string.Equals(selectedSceneItem.entryPath, scene, StringComparison.OrdinalIgnoreCase)) SelectPackage(p);
            ClearPendingDeferredScene();
            if (scenePrewarmCoroutine != null) {
                try { StopCoroutine(scenePrewarmCoroutine); } catch {}
                scenePrewarmCoroutine = null;
            }
            int autoDeleted = 0;
            if(autoCleanLinksBeforeSceneLoad){
                StopScenePrewarm(true);
                autoDeleted = ClearGeneratedLinksForSceneLoad();
                if(autoDeleted > 0) ScanAddonLightweight();
            }
            Stopwatch linkSw = Stopwatch.StartNew();
            Stopwatch rootDepsSw = Stopwatch.StartNew();
            LinkResult result=LinkWithDeps(p);
            rootDepsSw.Stop();
            double sceneReadMs = 0, sceneRefsMs = 0, sceneVariantMs = 0, localizeMs = 0;
            string sceneJson = "";
            string sceneLoadPath = SceneRef(p, scene);
            SceneVariantResult sceneVariant = null;
            int localizedScripts = 0;
            bool localizedScriptsChanged = false;
            TimelineOptimizationInfo timelineInfo = new TimelineOptimizationInfo();
            List<string> localizationErrors = new List<string>();
            try {
                Stopwatch stepSw = Stopwatch.StartNew();
                if (selectedSceneAnalysis != null && string.Equals(selectedSceneAnalysis.key, requestedSceneKey, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(selectedSceneAnalysis.json)) {
                    sceneJson = selectedSceneAnalysis.json;
                    timelineInfo.cacheHit = true;
                    timelineInfo.optimized = sceneJson.IndexOf("\"SerializeMode\":\"2\"", StringComparison.Ordinal) >= 0;
                    timelineInfo.outputBytes = Encoding.UTF8.GetByteCount(sceneJson);
                }
                else {
                    sceneJson = ReadSceneJsonWithTimelineOptimization(p, scene, out timelineInfo);
                }
                if (!string.IsNullOrEmpty(sceneJson)) {
                    DebugLog("LoadPackageScene scene-json read OK. chars=" + sceneJson.Length + ", cached=" + (selectedSceneAnalysis != null && string.Equals(selectedSceneAnalysis.key, requestedSceneKey, StringComparison.OrdinalIgnoreCase)) + ", scene=" + scene + ", uid=" + p.uid);
                    stepSw.Stop();
                    sceneReadMs = stepSw.Elapsed.TotalMilliseconds;
                    selectedSceneAnalysis = ReadAndAnalyzeScene(p, scene, sceneJson);
                    if (selectedSceneAnalysis.personIds.Count > 0 && !selectedSceneAnalysis.personIds.Contains(scenePrimaryPersonId)) scenePrimaryPersonId = selectedSceneAnalysis.personIds[0];
                    stepSw = Stopwatch.StartNew();
                    PresetLinkDiag sceneDiag = AutoLinkSceneDepsDetailed(sceneJson);
                    stepSw.Stop();
                    sceneRefsMs = stepSw.Elapsed.TotalMilliseconds;
                    result.created += sceneDiag.linked;
                    result.already += sceneDiag.already;
                    for (int i = 0; i < sceneDiag.missing.Count; i++) if (!result.missing.Contains(sceneDiag.missing[i])) result.missing.Add(sceneDiag.missing[i]);
                    for (int i = 0; i < sceneDiag.errors.Count; i++) result.errors.Add(sceneDiag.errors[i]);
                    string preparedSceneJson = sceneJson;
                    if (sceneLoadMode > 0) {
                        stepSw = Stopwatch.StartNew();
                        string variantError;
                        SceneVariantResult builtVariant;
                        if (TryBuildSceneVariants(selectedSceneAnalysis, sceneLoadMode, scenePrimaryPersonId, out builtVariant, out variantError)) {
                            sceneVariant = builtVariant;
                            preparedSceneJson = builtVariant.primaryJson;
                            DebugLog("Scene variant built: mode=" + SceneLoadModeName(sceneLoadMode) + ", primary=" + scenePrimaryPersonId + ", total=" + builtVariant.totalAtoms + ", kept=" + builtVariant.keptAtoms + ", deferred=" + builtVariant.deferredAtoms + ", deferredTypes=" + string.Join(",", builtVariant.deferredTypes.ToArray()));
                        } else {
                            DebugLog("Scene variant fallback to full: " + variantError);
                            SetStatus("人物优先分析失败，已回退完整加载：" + variantError, true);
                        }
                        stepSw.Stop();
                        sceneVariantMs = stepSw.Elapsed.TotalMilliseconds;
                    }
                    PrepareLazyCuaCandidates(preparedSceneJson);
                    try {
                        stepSw = Stopwatch.StartNew();
                        bool primaryScriptsChanged;
                        string localScene = MaterializeSceneWithLocalScripts(p, scene, preparedSceneJson, out localizedScripts, out localizationErrors, out primaryScriptsChanged);
                        localizedScriptsChanged |= primaryScriptsChanged;
                        if (string.IsNullOrEmpty(localScene) && sceneVariant != null && sceneVariant.deferredAtoms > 0) localScene = WritePreparedSceneTemp(p, scene, preparedSceneJson, "primary");
                        stepSw.Stop();
                        localizeMs = stepSw.Elapsed.TotalMilliseconds;
                        if (!string.IsNullOrEmpty(localScene)) sceneLoadPath = localScene;
                        for (int le = 0; le < localizationErrors.Count; le++) DebugLog("Scene script localization issue: " + localizationErrors[le]);
                        if (sceneVariant != null && sceneVariant.deferredAtoms > 0 && !string.IsNullOrEmpty(sceneVariant.deferredJson)) {
                            int deferredLocalized;
                            List<string> deferredErrors;
                            bool deferredScriptsChanged;
                            string deferredEntry = Path.GetFileNameWithoutExtension(scene) + "__deferred.json";
                            string deferredPath = MaterializeSceneWithLocalScripts(p, deferredEntry, sceneVariant.deferredJson, out deferredLocalized, out deferredErrors, out deferredScriptsChanged);
                            localizedScriptsChanged |= deferredScriptsChanged;
                            localizedScripts += deferredLocalized;
                            for (int de = 0; de < deferredErrors.Count; de++) localizationErrors.Add("deferred: " + deferredErrors[de]);
                            if (string.IsNullOrEmpty(deferredPath)) deferredPath = WritePreparedSceneTemp(p, scene, sceneVariant.deferredJson, "deferred");
                            pendingDeferredScenePath = deferredPath;
                            pendingDeferredAtomCount = sceneVariant.deferredAtoms;
                        }
                    } catch(Exception locEx) {
                        sceneLoadPath = SceneRef(p, scene);
                        sceneVariant = null;
                        localizedScripts = 0;
                        ClearPendingDeferredScene();
                        localizationErrors.Add(locEx.Message);
                        DebugLog("Scene script localization failed; falling back to the original full scene: " + locEx.ToString());
                    }
                } else {
                    stepSw.Stop();
                    sceneReadMs = stepSw.Elapsed.TotalMilliseconds;
                    DebugLog("LoadPackageScene scene-json prelink skipped: unable to read " + scene + " from " + p.uid);
                }
            } catch(Exception e) {
                DebugLog("LoadPackageScene scene-json prelink failed: " + e.Message);
            }
            linkSw.Stop();
            double refreshMs = 0;
            if(result.created>0 || autoDeleted>0 || localizedScriptsChanged){
                Stopwatch refreshSw = Stopwatch.StartNew();
                RefreshVam();
                refreshSw.Stop();
                refreshMs = refreshSw.Elapsed.TotalMilliseconds;
            } else {
                DebugLog("LoadPackageScene fast path: all links already available, skip RefreshVam/RescanPackages.");
            }
            string sceneRef=SceneRef(p,scene);
            string msg="正在加载场景 "+sceneRef+" | 清理旧链接="+autoDeleted+"，新建链接="+result.created+"，已存在="+result.already+"，缺失依赖="+result.missing.Count+"，错误="+result.errors.Count+" | 准备="+totalSw.Elapsed.TotalSeconds.ToString("0.0")+"s";
            if(sceneVariant!=null && sceneVariant.deferredAtoms>0) msg+=" | "+SceneLoadModeName(sceneLoadMode)+"="+sceneVariant.keptAtoms+"/"+sceneVariant.totalAtoms+"，其余="+sceneVariant.deferredAtoms;
            if(localizedScripts>0) msg+=" | 本地化脚本="+localizedScripts;
            if(localizationErrors.Count>0) msg+=" | 脚本本地化异常="+localizationErrors.Count;
            if(result.missing.Count>0) msg+=" | 缺失："+string.Join(", ",result.missing.ToArray());
            if(result.errors.Count>0) msg+=" | 错误："+string.Join("；",result.errors.ToArray());
            SetStatus(msg,true);
            DebugLog("LoadPackageScene prep timings: autoDeleted="+autoDeleted+", rootDepsMs="+rootDepsSw.Elapsed.TotalMilliseconds.ToString("0")+", sceneReadMs="+sceneReadMs.ToString("0")+", timelineCacheHit="+timelineInfo.cacheHit+", timelineOptimized="+timelineInfo.optimized+", timelineReadMs="+timelineInfo.readMs.ToString("0")+", timelineOptimizeMs="+timelineInfo.optimizeMs.ToString("0")+", timelineCacheReadMs="+timelineInfo.cacheReadMs.ToString("0")+", timelineSourceBytes="+timelineInfo.sourceBytes+", timelineOutputBytes="+timelineInfo.outputBytes+", timelineAnimations="+timelineInfo.animations+", timelineCurves="+timelineInfo.curves+", timelineKeys="+timelineInfo.keyframes+", sceneRefsMs="+sceneRefsMs.ToString("0")+", sceneVariantMs="+sceneVariantMs.ToString("0")+", localizeMs="+localizeMs.ToString("0")+", linkMs="+linkSw.Elapsed.TotalMilliseconds.ToString("0")+", refreshMs="+refreshMs.ToString("0")+", totalMs="+totalSw.Elapsed.TotalMilliseconds.ToString("0")+", created="+result.created+", already="+result.already+", missing="+result.missing.Count+", errors="+result.errors.Count+", localizedScripts="+localizedScripts+", localizedScriptsChanged="+localizedScriptsChanged+", localizationErrors="+localizationErrors.Count+", mode="+SceneLoadModeName(sceneLoadMode)+", primary="+scenePrimaryPersonId+", deferred="+pendingDeferredAtomCount+", sceneLoadPath="+sceneLoadPath);
            int expectedAtoms = sceneVariant != null ? sceneVariant.keptAtoms
                : (selectedSceneAnalysis != null && string.Equals(selectedSceneAnalysis.key, requestedSceneKey, StringComparison.OrdinalIgnoreCase) ? selectedSceneAnalysis.atoms.Count : 0);
            if(result.errors.Count==0 && SuperController.singleton!=null) ScheduleSceneLoad(sceneLoadPath, expectedAtoms);
            else {
                ClearPendingDeferredScene();
                if (SuperController.singleton == null) SetStatus("加载场景失败：SuperController 为空 | " + sceneRef, true);
                if(canvas!=null) UpdateInspectorVisibility();
            }
        }catch(Exception e){
            CancelPendingSceneLoad();
            ClearPendingDeferredScene();
            StopScenePrewarm(true);
            if(canvas!=null) UpdateInspectorVisibility();
            DebugLog("LoadPackageScene FAILED: "+e.ToString());
            SetStatus("加载场景失败："+e.Message,true);
        }
    }
    private string WritePreparedSceneTemp(PackageLite p, string scene, string json, string tag) {
        string dir = Path.Combine(vamRoot, "Saves\\scene\\_AllPackagesLinkerTempScenes");
        Directory.CreateDirectory(dir);
        string baseName = SafeFileName((p == null ? "scene" : p.uid) + "__" + Path.GetFileNameWithoutExtension(scene) + "__" + tag + ".json");
        if (!baseName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) baseName += ".json";
        string outPath = Path.Combine(dir, baseName);
        File.WriteAllText(outPath, json, Encoding.UTF8);
        DebugLog("Prepared scene temp written: tag=" + tag + ", chars=" + json.Length + ", out=" + outPath);
        return Norm(MakeRel(vamRoot, outPath));
    }
    private string SceneRef(PackageLite p,string scene){ return p.uid+":/"+Norm(scene); }
    private void CancelPendingSceneLoad(){
        CancelInvoke("DoDelayedSceneLoad");
        CancelInvoke("TryDispatchSceneLoadAfterPrewarm");
        pendingScenePath="";
        pendingSceneExpectedAtomCount=0;
    }
    private void ClearPendingDeferredScene(){ pendingDeferredScenePath=""; pendingDeferredAtomCount=0; }
    private void ScheduleSceneLoad(string scenePath, int expectedAtoms){
        CancelPendingSceneLoad();
        pendingScenePath=scenePath;
        pendingSceneExpectedAtomCount=Math.Max(0,expectedAtoms);
        DebugLog("Scene load scheduled: "+scenePath+" | expectedAtoms="+pendingSceneExpectedAtomCount+", loadDelay="+SceneLoadDispatchDelay.ToString("0.00")+"s; package refresh already completed synchronously when needed.");
        CancelInvoke("DoDelayedSceneLoad");
        CancelInvoke("TryDispatchSceneLoadAfterPrewarm");
        if(scenePrewarmPending>0 && !string.IsNullOrEmpty(scenePrewarmKey)){
            scenePrewarmWaitUntil=Time.realtimeSinceStartup+8.0f;
            DebugLog("Scene load waiting for active skin prewarm. pending="+scenePrewarmPending+", timeout=8.0s");
            Invoke("TryDispatchSceneLoadAfterPrewarm",SceneLoadDispatchDelay);
        } else Invoke("DoDelayedSceneLoad",SceneLoadDispatchDelay);
    }
    private void TryDispatchSceneLoadAfterPrewarm(){
        if(scenePrewarmPending>0 && Time.realtimeSinceStartup<scenePrewarmWaitUntil){ Invoke("TryDispatchSceneLoadAfterPrewarm",0.10f); return; }
        bool timedOut=scenePrewarmPending>0;
        DebugLog("Scene prewarm wait ended. pending="+scenePrewarmPending+", timedOut="+timedOut);
        if(timedOut) StopScenePrewarm(true);
        DoDelayedSceneLoad();
    }
    private void DoDelayedSceneLoad() {
        string scenePath = pendingScenePath;
        int expectedAtoms = pendingSceneExpectedAtomCount;
        pendingScenePath = "";
        pendingSceneExpectedAtomCount = 0;
        Stopwatch loadSw = Stopwatch.StartNew();
        try {
            DebugLog("Calling SuperController.Load: " + scenePath + ", superController=" + (SuperController.singleton != null));
            if (string.IsNullOrEmpty(scenePath)) throw new InvalidOperationException("待加载场景路径为空");
            if (SuperController.singleton != null) {
                BeginSceneLoadProfile(scenePath, expectedAtoms);
                SuperController.singleton.Load(scenePath);
                loadSw.Stop();
                DebugLog("SuperController.Load returned: path=" + scenePath + ", elapsedMs=" + loadSw.Elapsed.TotalMilliseconds.ToString("0"));
                PollSceneLoadProfile(true);
                CancelInvoke("PatchLoadedPackageScriptPluginUrls");
                Invoke("PatchLoadedPackageScriptPluginUrls", 3.0f);
                Invoke("PatchLoadedPackageScriptPluginUrls", 6.0f);
                Invoke("PatchLoadedPackageScriptPluginUrls", 10.0f);
                if (autoAllowAllPlugins) {
                    CancelInvoke("AutoAllowAllPendingPluginPackages");
                    Invoke("AutoAllowAllPendingPluginPackages", 1.0f);
                    Invoke("AutoAllowAllPendingPluginPackages", 2.5f);
                    Invoke("AutoAllowAllPendingPluginPackages", 5.0f);
                }
            } else {
                ClearPendingDeferredScene();
                if (canvas != null) UpdateInspectorVisibility();
                SetStatus("加载场景失败：SuperController 为空 | " + scenePath, true);
            }
        } catch(Exception e) {
            loadSw.Stop();
            FinishSceneLoadProfile("load-call-failed");
            ClearPendingDeferredScene();
            if (canvas != null) UpdateInspectorVisibility();
            DebugLog("DoDelayedSceneLoad FAILED after " + loadSw.Elapsed.TotalMilliseconds.ToString("0") + "ms: " + e.ToString());
            SetStatus("加载场景失败：" + e.Message + " | " + scenePath, true);
        }
    }
    private void BeginSceneLoadProfile(string scenePath, int expectedAtoms) {
        if (sceneLoadProfileActive) FinishSceneLoadProfile("replaced-by-new-load");
        sceneLoadProfileActive = true;
        sceneLoadProfileSeenLoading = false;
        sceneLoadProfileLastLoading = false;
        sceneLoadProfileStartedAt = Time.realtimeSinceStartup;
        sceneLoadProfileNextPollAt = sceneLoadProfileStartedAt;
        sceneLoadProfileLastChangeAt = sceneLoadProfileStartedAt;
        sceneLoadProfileExpectedAtoms = Math.Max(0, expectedAtoms);
        sceneLoadProfileLastAtomCount = -1;
        sceneLoadProfileChangeEvents = 0;
        sceneLoadProfilePath = scenePath ?? "";
        sceneLoadProfileLastAdded = "";
        sceneLoadProfileAtoms.Clear();
        sceneLoadProfileHoldsInitialized = false;
        sceneLoadProfileMaxPendingHolds = 0;
        sceneLoadProfileLongestHoldSeconds = 0f;
        sceneLoadProfileLongestHold = "";
        sceneLoadProfileLastCompletedHold = "";
        sceneLoadProfileAssetCallbacks = 0;
        sceneLoadProfileOutOfOrderCallbacks = 0;
        sceneLoadProfileMaxCallbackScanAhead = 0;
        sceneLoadProfileAssetCallbackWorkMs = 0.0;
        sceneLoadProfileSlowestAssetCallbackMs = 0.0;
        sceneLoadProfileSlowestAssetCallback = "";
        sceneLoadProfileDeferredCua = 0;
        sceneLoadProfileActivatedDeferredCua = 0;
        sceneLoadProfilePendingHolds.Clear();
        sceneLoadProfileHoldFirstSeen.Clear();
        sceneLoadProfileHoldLabels.Clear();
        DebugLog("[SceneLoadProfile] begin path=" + sceneLoadProfilePath + ", expectedAtoms=" + sceneLoadProfileExpectedAtoms);
    }
    private void PollSceneLoadProfile(bool force) {
        if (!sceneLoadProfileActive) return;
        float now = Time.realtimeSinceStartup;
        if (!force && now < sceneLoadProfileNextPollAt) return;
        sceneLoadProfileNextPollAt = now + SceneLoadProfilePollInterval;
        try {
            SuperController sc = SuperController.singleton;
            if (sc == null) { FinishSceneLoadProfile("super-controller-missing"); return; }
            bool loading = sc.isLoading;
            if (loading) sceneLoadProfileSeenLoading = true;
            if (sceneLoadProfileLastAtomCount < 0 || loading != sceneLoadProfileLastLoading) {
                DebugLog("[SceneLoadProfile] loading=" + loading + ", elapsedMs=" + SceneLoadProfileElapsedMs(now).ToString("0"));
                sceneLoadProfileLastLoading = loading;
            }
            PollSceneHoldLoadProfile(sc, now);

            List<Atom> atoms = sc.GetAtoms();
            HashSet<string> current = new HashSet<string>(StringComparer.Ordinal);
            Dictionary<string, int> types = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> added = new List<string>();
            List<string> removed = new List<string>();
            if (atoms != null) {
                for (int i = 0; i < atoms.Count; i++) {
                    Atom atom = atoms[i];
                    if (atom == null) continue;
                    string uid = atom.uid ?? "";
                    string type = string.IsNullOrEmpty(atom.type) ? "?" : atom.type;
                    string key = uid + ListSep + type;
                    current.Add(key);
                    int typeCount;
                    types.TryGetValue(type, out typeCount);
                    types[type] = typeCount + 1;
                    if (sceneLoadProfileLastAtomCount >= 0 && !sceneLoadProfileAtoms.Contains(key)) added.Add(type + ":" + uid);
                }
            }
            if (sceneLoadProfileLastAtomCount >= 0) {
                foreach (string key in sceneLoadProfileAtoms) {
                    if (!current.Contains(key)) removed.Add(SceneLoadProfileAtomLabel(key));
                }
            }
            bool atomsChanged = sceneLoadProfileLastAtomCount < 0 || current.Count != sceneLoadProfileLastAtomCount || added.Count > 0 || removed.Count > 0;
            if (atomsChanged) {
                sceneLoadProfileLastChangeAt = now;
                sceneLoadProfileChangeEvents++;
                if (added.Count > 0) sceneLoadProfileLastAdded = added[added.Count - 1];
                DebugLog("[SceneLoadProfile] atoms elapsedMs=" + SceneLoadProfileElapsedMs(now).ToString("0")
                    + ", count=" + current.Count + "/" + sceneLoadProfileExpectedAtoms
                    + ", added=" + SceneLoadProfileList(added, 12)
                    + ", removed=" + SceneLoadProfileList(removed, 12)
                    + ", types=" + SceneLoadProfileTypes(types));
                sceneLoadProfileAtoms = current;
                sceneLoadProfileLastAtomCount = current.Count;
            }

            float elapsed = now - sceneLoadProfileStartedAt;
            float stable = now - sceneLoadProfileLastChangeAt;
            if (sceneLoadProfileSeenLoading && !loading && stable >= SceneLoadProfileStableSeconds) FinishSceneLoadProfile("loading-finished");
            else if (!sceneLoadProfileSeenLoading && sceneLoadProfileExpectedAtoms > 0 && current.Count >= sceneLoadProfileExpectedAtoms && stable >= SceneLoadProfileStableSeconds) FinishSceneLoadProfile("expected-atoms-stable");
            else if (!sceneLoadProfileSeenLoading && !loading && elapsed >= SceneLoadProfileNoLoadingGraceSeconds && stable >= SceneLoadProfileNoLoadingGraceSeconds) FinishSceneLoadProfile("no-loading-observed-stable");
            else if (elapsed >= SceneLoadProfileTimeoutSeconds) FinishSceneLoadProfile("timeout");
        } catch(Exception e) {
            DebugLog("[SceneLoadProfile] poll failed: " + e.Message);
            if (Time.realtimeSinceStartup - sceneLoadProfileStartedAt >= SceneLoadProfileTimeoutSeconds) FinishSceneLoadProfile("timeout-after-poll-error");
        }
    }
    private void PollSceneHoldLoadProfile(SuperController sc, float now) {
        if (object.ReferenceEquals(sceneHoldLoadCompleteFlagsField, null)) sceneHoldLoadCompleteFlagsField = typeof(SuperController).GetField("holdLoadCompleteFlags", BindingFlags.Instance | BindingFlags.NonPublic);
        if (object.ReferenceEquals(sceneHoldLoadCompleteFlagsField, null)) {
            if (!sceneLoadProfileHoldsInitialized) DebugLog("[SceneLoadProfile] holds unavailable: holdLoadCompleteFlags field not found");
            sceneLoadProfileHoldsInitialized = true;
            return;
        }
        List<AsyncFlag> flags = sceneHoldLoadCompleteFlagsField.GetValue(sc) as List<AsyncFlag>;
        if (flags == null) {
            return;
        }

        HashSet<AsyncFlag> pending = new HashSet<AsyncFlag>();
        for (int i = 0; i < flags.Count; i++) {
            AsyncFlag flag = flags[i];
            if (flag == null) continue;
            if (!sceneLoadProfileHoldFirstSeen.ContainsKey(flag)) sceneLoadProfileHoldFirstSeen[flag] = now;
            if (!flag.Raised) pending.Add(flag);
        }
        if (pending.Count > sceneLoadProfileMaxPendingHolds) sceneLoadProfileMaxPendingHolds = pending.Count;

        bool changed = !sceneLoadProfileHoldsInitialized || !pending.SetEquals(sceneLoadProfilePendingHolds);
        if (!changed) {
            UpdateSceneLongestHold(pending, now);
            return;
        }

        RefreshSceneCuaHoldLabels(pending);
        List<string> completed = new List<string>();
        foreach (AsyncFlag flag in sceneLoadProfilePendingHolds) {
            if (pending.Contains(flag)) continue;
            float waitSeconds = SceneHoldWaitSeconds(flag, now);
            string label = SceneHoldLabel(flag);
            completed.Add(label + "@" + (waitSeconds * 1000f).ToString("0") + "ms");
            sceneLoadProfileLastCompletedHold = label;
            UpdateSceneLongestHold(label, waitSeconds);
        }
        List<string> waiting = new List<string>();
        foreach (AsyncFlag flag in pending) {
            float waitSeconds = SceneHoldWaitSeconds(flag, now);
            string label = SceneHoldLabel(flag);
            waiting.Add(label + "@" + (waitSeconds * 1000f).ToString("0") + "ms");
            UpdateSceneLongestHold(label, waitSeconds);
        }
        waiting.Sort(StringComparer.OrdinalIgnoreCase);
        completed.Sort(StringComparer.OrdinalIgnoreCase);
        DebugLog("[SceneLoadProfile] holds elapsedMs=" + SceneLoadProfileElapsedMs(now).ToString("0")
            + ", tracked=" + flags.Count
            + ", pending=" + pending.Count
            + ", completed=" + SceneLoadProfileList(completed, 12)
            + ", waiting=" + SceneLoadProfileList(waiting, 12));
        sceneLoadProfilePendingHolds = pending;
        sceneLoadProfileHoldsInitialized = true;
    }
    private void RefreshSceneCuaHoldLabels(HashSet<AsyncFlag> pending) {
        bool needsRefresh = false;
        foreach (AsyncFlag flag in pending) {
            if (!sceneLoadProfileHoldLabels.ContainsKey(flag)) { needsRefresh = true; break; }
        }
        if (!needsRefresh) return;
        if (object.ReferenceEquals(sceneCuaLoadingFlagField, null)) sceneCuaLoadingFlagField = typeof(CustomUnityAssetLoader).GetField("isLoadingFlag", BindingFlags.Instance | BindingFlags.NonPublic);
        if (object.ReferenceEquals(sceneCuaAssetUrlField, null)) sceneCuaAssetUrlField = typeof(CustomUnityAssetLoader).GetField("assetUrlJSON", BindingFlags.Instance | BindingFlags.NonPublic);
        if (object.ReferenceEquals(sceneCuaResolvedUrlField, null)) sceneCuaResolvedUrlField = typeof(CustomUnityAssetLoader).GetField("assetBundleUrl", BindingFlags.Instance | BindingFlags.NonPublic);
        if (object.ReferenceEquals(sceneCuaLoadingFlagField, null)) return;
        try {
            CustomUnityAssetLoader[] loaders = UnityEngine.Object.FindObjectsOfType<CustomUnityAssetLoader>();
            for (int i = 0; i < loaders.Length; i++) {
                CustomUnityAssetLoader loader = loaders[i];
                if (loader == null) continue;
                AsyncFlag flag = sceneCuaLoadingFlagField.GetValue(loader) as AsyncFlag;
                if (flag == null || sceneLoadProfileHoldLabels.ContainsKey(flag)) continue;
                string uid = loader.containingAtom == null ? "?" : loader.containingAtom.uid;
                string url = "";
                if (!object.ReferenceEquals(sceneCuaAssetUrlField, null)) {
                    JSONStorableUrl urlParam = sceneCuaAssetUrlField.GetValue(loader) as JSONStorableUrl;
                    if (urlParam != null) url = urlParam.val;
                }
                if (string.IsNullOrEmpty(url) && !object.ReferenceEquals(sceneCuaResolvedUrlField, null)) url = sceneCuaResolvedUrlField.GetValue(loader) as string;
                sceneLoadProfileHoldLabels[flag] = "CUA:" + SceneLoadProfileLogValue(uid) + "=>" + SceneLoadProfileLogValue(url);
            }
        } catch (Exception e) {
            DebugLog("[SceneLoadProfile] CUA hold label lookup failed: " + e.Message);
        }
    }
    private float SceneHoldWaitSeconds(AsyncFlag flag, float now) {
        float firstSeen;
        return flag != null && sceneLoadProfileHoldFirstSeen.TryGetValue(flag, out firstSeen) ? Math.Max(0f, now - firstSeen) : 0f;
    }
    private string SceneHoldLabel(AsyncFlag flag) {
        if (flag == null) return "?";
        string label;
        if (sceneLoadProfileHoldLabels.TryGetValue(flag, out label) && !string.IsNullOrEmpty(label)) return label;
        return SceneLoadProfileLogValue(string.IsNullOrEmpty(flag.Name) ? "unnamed" : flag.Name);
    }
    private void UpdateSceneLongestHold(HashSet<AsyncFlag> pending, float now) {
        foreach (AsyncFlag flag in pending) UpdateSceneLongestHold(SceneHoldLabel(flag), SceneHoldWaitSeconds(flag, now));
    }
    private void UpdateSceneLongestHold(string label, float waitSeconds) {
        if (waitSeconds <= sceneLoadProfileLongestHoldSeconds) return;
        sceneLoadProfileLongestHoldSeconds = waitSeconds;
        sceneLoadProfileLongestHold = label;
    }
    private static string SceneLoadProfileLogValue(string value) {
        if (string.IsNullOrEmpty(value)) return "?";
        string clean = value.Replace('\r', ' ').Replace('\n', ' ').Replace('|', '/');
        return clean.Length <= 320 ? clean : clean.Substring(0, 317) + "...";
    }
    private double SceneLoadProfileElapsedMs(float now) { return Math.Max(0f, now - sceneLoadProfileStartedAt) * 1000.0; }
    private static string SceneLoadProfileAtomLabel(string key) {
        int split = key.IndexOf(ListSep, StringComparison.Ordinal);
        if (split < 0) return key;
        return key.Substring(split + ListSep.Length) + ":" + key.Substring(0, split);
    }
    private static string SceneLoadProfileList(List<string> values, int limit) {
        if (values == null || values.Count == 0) return "-";
        int count = Math.Min(limit, values.Count);
        string[] shown = new string[count];
        for (int i = 0; i < count; i++) shown[i] = values[i];
        return string.Join("|", shown) + (values.Count > count ? "|+" + (values.Count - count) : "");
    }
    private static string SceneLoadProfileTypes(Dictionary<string, int> types) {
        if (types == null || types.Count == 0) return "-";
        List<string> names = new List<string>(types.Keys);
        names.Sort(StringComparer.OrdinalIgnoreCase);
        string[] parts = new string[names.Count];
        for (int i = 0; i < names.Count; i++) parts[i] = names[i] + ":" + types[names[i]];
        return string.Join("|", parts);
    }
    private void FinishSceneLoadProfile(string reason) {
        if (!sceneLoadProfileActive) return;
        float now = Time.realtimeSinceStartup;
        DebugLog("[SceneLoadProfile] complete reason=" + reason
            + ", elapsedMs=" + SceneLoadProfileElapsedMs(now).ToString("0")
            + ", loadingSeen=" + sceneLoadProfileSeenLoading
            + ", count=" + Math.Max(0, sceneLoadProfileLastAtomCount) + "/" + sceneLoadProfileExpectedAtoms
            + ", changeEvents=" + sceneLoadProfileChangeEvents
            + ", lastChangeMs=" + SceneLoadProfileElapsedMs(sceneLoadProfileLastChangeAt).ToString("0")
            + ", lastAdded=" + (string.IsNullOrEmpty(sceneLoadProfileLastAdded) ? "-" : sceneLoadProfileLastAdded)
            + ", maxPendingHolds=" + sceneLoadProfileMaxPendingHolds
            + ", deferredCua=" + sceneLoadProfileDeferredCua
            + ", activatedDeferredCua=" + sceneLoadProfileActivatedDeferredCua
            + ", remainingDeferredCua=" + deferredCuaUrls.Count
            + ", longestHoldMs=" + (sceneLoadProfileLongestHoldSeconds * 1000f).ToString("0")
            + ", longestHold=" + (string.IsNullOrEmpty(sceneLoadProfileLongestHold) ? "-" : sceneLoadProfileLongestHold)
            + ", lastCompletedHold=" + (string.IsNullOrEmpty(sceneLoadProfileLastCompletedHold) ? "-" : sceneLoadProfileLastCompletedHold)
            + ", callbackGear=" + AssetCallbackGearName(assetCallbackGear)
            + ", assetWorkers=" + AssetWorkerCountForGear(assetCallbackGear)
            + ", callbacks=" + sceneLoadProfileAssetCallbacks
            + ", outOfOrderCallbacks=" + sceneLoadProfileOutOfOrderCallbacks
            + ", maxCallbackScanAhead=" + sceneLoadProfileMaxCallbackScanAhead
            + ", callbackWorkMs=" + sceneLoadProfileAssetCallbackWorkMs.ToString("0")
            + ", slowestCallbackMs=" + sceneLoadProfileSlowestAssetCallbackMs.ToString("0")
            + ", slowestCallback=" + (string.IsNullOrEmpty(sceneLoadProfileSlowestAssetCallback) ? "-" : sceneLoadProfileSlowestAssetCallback)
            + ", path=" + sceneLoadProfilePath);
        sceneLoadProfileActive = false;
        sceneLoadProfileAtoms.Clear();
        sceneLoadProfilePendingHolds.Clear();
        sceneLoadProfileHoldFirstSeen.Clear();
        sceneLoadProfileHoldLabels.Clear();
    }
    private void LoadDeferredSceneAtoms(){
        string path=pendingDeferredScenePath;
        int count=pendingDeferredAtomCount;
        if(string.IsNullOrEmpty(path)||count<=0){SetStatus("没有等待加载的其余 Atom。",false);return;}
        try{
            if(SuperController.singleton==null){SetStatus("加载其余 Atom 失败：SuperController 为空。",true);return;}
            DebugLog("Calling SuperController.LoadMerge: "+path+", atoms="+count);
            pendingDeferredScenePath=""; pendingDeferredAtomCount=0;
            UpdateInspectorVisibility();
            SuperController.singleton.LoadMerge(path);
            SetStatus("正在合并加载其余 "+count+" 个 Atom...",true);
            CancelInvoke("PatchLoadedPackageScriptPluginUrls");
            Invoke("PatchLoadedPackageScriptPluginUrls",3.0f);
            Invoke("PatchLoadedPackageScriptPluginUrls",6.0f);
            Invoke("PatchLoadedPackageScriptPluginUrls",10.0f);
        }catch(Exception e){
            pendingDeferredScenePath=path; pendingDeferredAtomCount=count;
            DebugLog("LoadDeferredSceneAtoms FAILED: "+e.ToString());
            SetStatus("加载其余 Atom 失败："+e.Message,true);
            UpdateInspectorVisibility();
        }
    }
    private LinkResult LinkWithDeps(PackageLite rootp){ LinkResult result=new LinkResult(); var todo=new List<PackageLite>(); var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var exactAliases=new Dictionary<string,PackageLite>(StringComparer.OrdinalIgnoreCase); Collect(rootp,todo,seen,result.missing,exactAliases); Directory.CreateDirectory(linkRoot); foreach(var p in todo){ bool exactRoot = rootp!=null && string.Equals(p.uid,rootp.uid,StringComparison.OrdinalIgnoreCase); if(exactRoot ? IsExactAvailableInAddon(p.uid) : IsAvailableInAddon(p.uid)){result.already++;continue;} try{ if(LinkOne(p))result.created++; }catch(Exception e){result.errors.Add(p.uid+":"+e.Message);} } foreach(KeyValuePair<string,PackageLite> alias in exactAliases){try{if(IsExactAvailableInAddon(alias.Key)){result.already++;continue;}if(LinkExactAlias(alias.Key,alias.Value))result.created++;}catch(Exception e){result.errors.Add(alias.Key+"=>"+(alias.Value==null?"null":alias.Value.uid)+":"+e.Message);}} return result; }
    private void Collect(PackageLite p,List<PackageLite> todo,HashSet<string> seen,List<string> miss,Dictionary<string,PackageLite> exactAliases){ if(p==null||!seen.Add(p.uid))return; todo.Add(p); foreach(string d in p.deps){ PackageLite r; bool already; string source; if(!TryResolveDepDetailed(d,out r,out already,out source)){if(!miss.Contains(d))miss.Add(d);continue;} if(!d.EndsWith(".latest",StringComparison.OrdinalIgnoreCase) && r!=null && !string.Equals(d,r.uid,StringComparison.OrdinalIgnoreCase) && source.IndexOf("compatible-newer-version",StringComparison.OrdinalIgnoreCase)>=0) exactAliases[d]=r; if(!already)Collect(r,todo,seen,miss,exactAliases);} }
    private bool ResolveDep(string d,out PackageLite p,out bool already){ string source; return TryResolveDepDetailed(d,out p,out already,out source); }
    private bool TryGetAvailableAddonPackage(string uid, out PackageLite p, out string source) {
        p = null;
        source = "";
        if (addonExact.TryGetValue(uid, out p) && CanOpenVarFile(p.fullPath)) { source = "addon-exact"; return true; }
        if (uid.EndsWith(".latest", StringComparison.OrdinalIgnoreCase) && addonLatest.TryGetValue(uid, out p) && CanOpenVarFile(p.fullPath)) { source = "addon-latest"; return true; }
        string latestKey = Group(uid) + ".latest";
        if (addonLatest.TryGetValue(latestKey, out p) && CanOpenVarFile(p.fullPath)) { source = "addon-latest-fallback"; return true; }
        p = null;
        return false;
    }
    private bool TryResolveDepDetailed(string d,out PackageLite p,out bool already,out string source){
        p=null;
        already=false;
        source="";
        if(d.EndsWith(".latest",StringComparison.OrdinalIgnoreCase)){
            if(addonLatest.TryGetValue(d,out p) && CanOpenVarFile(p.fullPath)){already=true;source="addon-latest";return true;}
            if(allLatest.TryGetValue(d,out p) && CanOpenVarFile(p.fullPath)){source="all-latest-cache";return true;}
            if(FindValidLatestInAll(d,out p)){source="all-latest-scan";return true;}
        } else {
            if(addonExact.TryGetValue(d,out p) && CanOpenVarFile(p.fullPath)){already=true;source="addon-exact";return true;}
            if(allExact.TryGetValue(d,out p) && CanOpenVarFile(p.fullPath)){source="all-exact-cache";return true;}
            if(FindValidExactInAll(d,out p)){source="all-exact-scan";return true;}
            string compatibleLatest=Group(d)+".latest";
            int requestedVersion=Version(d);
            if(addonLatest.TryGetValue(compatibleLatest,out p) && Version(p.uid)>=requestedVersion && CanOpenVarFile(p.fullPath)){already=true;source="addon-compatible-newer-version";DebugLog("Dependency compatible-version fallback: "+d+" -> "+p.uid+" via "+source);return true;}
            if(allLatest.TryGetValue(compatibleLatest,out p) && Version(p.uid)>=requestedVersion && CanOpenVarFile(p.fullPath)){source="all-compatible-newer-version-cache";DebugLog("Dependency compatible-version fallback: "+d+" -> "+p.uid+" via "+source);return true;}
            PackageLite compatiblePackage;
            if(FindValidLatestInAll(compatibleLatest,out compatiblePackage) && Version(compatiblePackage.uid)>=requestedVersion){p=compatiblePackage;source="all-compatible-newer-version-scan";DebugLog("Dependency compatible-version fallback: "+d+" -> "+p.uid+" via "+source);return true;}
        }
        p=null;
        return false;
    }
    private bool FindValidExactInAll(string uid,out PackageLite p){ p=null; for(int i=0;i<all.Count;i++){ PackageLite x=all[i]; if(x!=null && string.Equals(x.uid,uid,StringComparison.OrdinalIgnoreCase) && CanOpenVarFile(x.fullPath)){ p=x; allExact[uid]=x; return true; } } return false; }
    private bool FindValidLatestInAll(string latestKey,out PackageLite p){ p=null; int best=-1; for(int i=0;i<all.Count;i++){ PackageLite x=all[i]; if(x==null)continue; string k=Group(x.uid)+".latest"; if(!string.Equals(k,latestKey,StringComparison.OrdinalIgnoreCase))continue; if(!CanOpenVarFile(x.fullPath))continue; int v=Version(x.uid); if(p==null || v>best){ p=x; best=v; } } if(p!=null){ allLatest[latestKey]=p; return true; } return false; }
    private bool IsExactAvailableInAddon(string uid){ PackageLite p; return addonExact.TryGetValue(uid,out p) && CanOpenVarFile(p.fullPath); }
    private bool IsAvailableOutsideLinkRoot(string uid){ PackageLite p; if(!addonExact.TryGetValue(uid,out p) || !CanOpenVarFile(p.fullPath))return false; return !Path.GetFullPath(p.fullPath).StartsWith(Path.GetFullPath(linkRoot),StringComparison.OrdinalIgnoreCase); }
    private bool LinkExactAlias(string requestedUid,PackageLite source){
        if(string.IsNullOrEmpty(requestedUid)||source==null||!CanOpenVarFile(source.fullPath))throw new Exception("精确版本别名源包不可用");
        string dir=Path.Combine(linkRoot,"_ExactVersionAliases");
        Directory.CreateDirectory(dir);
        string link=Path.Combine(dir,SafeFileName(requestedUid)+".var");
        DeletePathIfExistsOrReparse(link);
        string apiOut;
        if(TryCreateFileSymlink(link,source.fullPath,out apiOut)){
            RegisterCreatedAddonLink(requestedUid,source,link);
            DebugLog("LinkExactAlias API symlink OK: "+requestedUid+" -> "+source.uid+" | "+link);
            return true;
        }
        string hardlinkOut;
        if(RunCmd("mklink /H "+Q(link)+" "+Q(source.fullPath),out hardlinkOut)){
            RegisterCreatedAddonLink(requestedUid,source,link);
            DebugLog("LinkExactAlias hardlink OK: "+requestedUid+" -> "+source.uid+" | "+link);
            return true;
        }
        try{
            File.Copy(source.fullPath,link,true);
            RegisterCreatedAddonLink(requestedUid,source,link);
            DebugLog("LinkExactAlias fallback COPY: "+requestedUid+" -> "+source.uid+" | "+link);
            return true;
        }catch(Exception e){throw new Exception("api="+apiOut+" | hardlink="+hardlinkOut+" | copy="+e.Message);}
    }
    private bool LinkOne(PackageLite p){
        if(p==null || !CanOpenVarFile(p.fullPath)) throw new Exception("源包不存在或不可读: "+(p==null?"null":p.fullPath));
        string link=Path.Combine(linkRoot,p.relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(link));
        DeletePathIfExistsOrReparse(link);
        string apiOut;
        if(TryCreateFileSymlink(link,p.fullPath,out apiOut)){
            RegisterCreatedAddonLink(p.uid,p,link);
            DebugLog("LinkOne API symlink OK: "+link+" -> "+p.fullPath);
            return true;
        }
        string symlinkOut;
        if(RunCmd("mklink "+Q(link)+" "+Q(p.fullPath),out symlinkOut)){
            RegisterCreatedAddonLink(p.uid,p,link);
            DebugLog("LinkOne mklink OK: "+link+" -> "+p.fullPath);
            return true;
        }
        string hardlinkOut;
        if(RunCmd("mklink /H "+Q(link)+" "+Q(p.fullPath),out hardlinkOut)){
            RegisterCreatedAddonLink(p.uid,p,link);
            DebugLog("LinkOne hardlink OK: "+link+" -> "+p.fullPath);
            return true;
        }
        try{
            DeletePathIfExistsOrReparse(link);
            DebugLog("LinkOne fallback COPY. api="+apiOut+", symlinkOut="+symlinkOut+", hardlinkOut="+hardlinkOut);
            File.Copy(p.fullPath,link,true);
            try{File.SetLastWriteTimeUtc(link,new DateTime(p.mtimeUtcTicks,DateTimeKind.Utc));}catch{}
            RegisterCreatedAddonLink(p.uid,p,link);
            return true;
        }catch(Exception copyEx){ throw new Exception("api symlink failed: "+apiOut+" | mklink failed: "+symlinkOut+" | hardlink failed: "+hardlinkOut+" | copy failed: "+copyEx.Message); }
    }
    private void RegisterCreatedAddonLink(string advertisedUid,PackageLite source,string link){
        PackageLite linked=new PackageLite();
        linked.uid=advertisedUid;
        linked.fullPath=Path.GetFullPath(link);
        linked.relPath=MakeRel(addonRoot,linked.fullPath);
        linked.size=source==null?0:source.size;
        linked.mtimeUtcTicks=source==null?0:source.mtimeUtcTicks;
        addonExact[advertisedUid]=linked;
        string latestKey=Group(advertisedUid)+".latest";
        PackageLite old;
        if(!addonLatest.TryGetValue(latestKey,out old)||Version(advertisedUid)>Version(old.uid)) addonLatest[latestKey]=linked;
    }
    private bool TryCreateFileSymlink(string link,string target,out string output){ output=""; try{ bool ok=CreateSymbolicLink(link,target,SYMBOLIC_LINK_FLAG_FILE|SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE); int err=Marshal.GetLastWin32Error(); output=ok?"ok":"win32="+err; return ok; }catch(Exception e){ output=e.Message; return false; } }
    private bool RunCmd(string cmd,out string output){ output=""; var psi=new ProcessStartInfo("cmd.exe","/c "+cmd); psi.CreateNoWindow=true; psi.UseShellExecute=false; psi.RedirectStandardOutput=true; psi.RedirectStandardError=true; using(Process pr=Process.Start(psi)){ if(!pr.WaitForExit(15000)){try{pr.Kill();}catch{} output="timeout"; return false;} output=(pr.StandardOutput.ReadToEnd()+" "+pr.StandardError.ReadToEnd()).Trim(); return pr.ExitCode==0; } }
    private string Q(string s){return "\""+s.Replace("\"","\"\"")+"\"";}
    private bool PathExistsOrReparse(string path){ try{ if(File.Exists(path)||Directory.Exists(path))return true; File.GetAttributes(path); return true; }catch{return false;} }
    private bool IsReparsePointPath(string path){ try{ return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0; }catch{return false;} }
    private void DeletePathIfExistsOrReparse(string path){ try{ if(!PathExistsOrReparse(path))return; FileAttributes a=File.GetAttributes(path); if((a&FileAttributes.Directory)!=0) Directory.Delete(path,false); else File.Delete(path); }catch(Exception e){ DebugLog("DeletePathIfExistsOrReparse failed "+path+": "+e.Message); } }
    private bool CanOpenVarFile(string path){ try{ if(string.IsNullOrEmpty(path))return false; using(FileStream fs=File.Open(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){ return fs!=null; } }catch{return false;} }
    private int CleanBrokenGeneratedLinks(){ int deleted=0,errors=0; try{ if(!Directory.Exists(linkRoot))return 0; string basePath=Path.GetFullPath(linkRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar; string[] files=Directory.GetFiles(linkRoot,"*.var",SearchOption.AllDirectories); for(int i=0;i<files.Length;i++){ string f=files[i]; string full=Path.GetFullPath(f); if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))continue; if(IsReparsePointPath(full) && !CanOpenVarFile(full)){ try{ File.Delete(full); deleted++; }catch(Exception e){ errors++; DebugLog("CleanBrokenGeneratedLinks delete failed "+full+": "+e.Message); } } } if(deleted>0)RemoveEmptyDirs(linkRoot); if(deleted>0||errors>0)DebugLog("CleanBrokenGeneratedLinks deleted="+deleted+", errors="+errors); }catch(Exception e){ DebugLog("CleanBrokenGeneratedLinks failed: "+e.Message); } return deleted; }

    private List<string> GetNonEssentialCacheDirectories() {
        HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        paths.Add(thumbRoot);
        paths.Add(Path.Combine(dataRoot, "timeline-cache"));
        paths.Add(Path.Combine(dataRoot, "temp_presets"));
        paths.Add(Path.Combine(vamRoot, "Custom\\Scripts\\_AllPackagesLinkerTemp"));
        paths.Add(Path.Combine(vamRoot, "Saves\\scene\\_AllPackagesLinkerTempScenes"));
        string[] presetTypes = new string[] { "Animation", "BreastPhysics", "Clothing", "Hair", "Morphs", "Plugins", "Pose", "Skin", "General", "Full", "Appearance" };
        for (int i = 0; i < presetTypes.Length; i++) paths.Add(Path.Combine(GetLocalPresetStoreDir(presetTypes[i]), "_AllPackagesLinkerTemp"));
        return new List<string>(paths);
    }

    private bool IsCachePathInsideVamRoot(string path) {
        try {
            string rootFull = Path.GetFullPath(vamRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string pathFull = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return pathFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    private static void MeasureCachePath(string path, ref long bytes, ref int files, ref int errors) {
        try {
            if (File.Exists(path)) {
                FileInfo file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) return;
                bytes += Math.Max(0L, file.Length);
                files++;
                return;
            }
            if (!Directory.Exists(path)) return;
            MeasureCacheDirectory(new DirectoryInfo(path), ref bytes, ref files, ref errors);
        } catch { errors++; }
    }

    private static void MeasureCacheDirectory(DirectoryInfo directory, ref long bytes, ref int files, ref int errors) {
        FileSystemInfo[] entries;
        try { entries = directory.GetFileSystemInfos(); }
        catch { errors++; return; }
        for (int i = 0; i < entries.Length; i++) {
            FileSystemInfo entry = entries[i];
            try {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                DirectoryInfo childDirectory = entry as DirectoryInfo;
                if (childDirectory != null) MeasureCacheDirectory(childDirectory, ref bytes, ref files, ref errors);
                else {
                    FileInfo file = entry as FileInfo;
                    if (file == null) continue;
                    bytes += Math.Max(0L, file.Length);
                    files++;
                }
            } catch { errors++; }
        }
    }

    private CacheUsageSnapshot MeasureCacheUsage() {
        CacheUsageSnapshot usage = new CacheUsageSnapshot();
        List<string> nonEssential = GetNonEssentialCacheDirectories();
        for (int i = 0; i < nonEssential.Count; i++) {
            if (!IsCachePathInsideVamRoot(nonEssential[i])) { usage.errors++; continue; }
            MeasureCachePath(nonEssential[i], ref usage.nonEssentialBytes, ref usage.nonEssentialFiles, ref usage.errors);
        }
        usage.allBytes = usage.nonEssentialBytes;
        usage.allFiles = usage.nonEssentialFiles;
        string vamCache = Path.Combine(vamRoot, "Cache");
        if (IsCachePathInsideVamRoot(vamCache)) MeasureCachePath(vamCache, ref usage.allBytes, ref usage.allFiles, ref usage.errors);
        else usage.errors++;
        return usage;
    }

    private static void DeleteCacheDirectoryContents(DirectoryInfo directory, CacheDeleteReport report) {
        FileSystemInfo[] entries;
        try { entries = directory.GetFileSystemInfos(); }
        catch { report.errors++; return; }
        for (int i = 0; i < entries.Length; i++) {
            FileSystemInfo entry = entries[i];
            try {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) {
                    DirectoryInfo linkedDirectory = entry as DirectoryInfo;
                    if (linkedDirectory != null) Directory.Delete(linkedDirectory.FullName, false);
                    else File.Delete(entry.FullName);
                    report.deletedFiles++;
                    continue;
                }
                DirectoryInfo childDirectory = entry as DirectoryInfo;
                if (childDirectory != null) {
                    DeleteCacheDirectoryContents(childDirectory, report);
                    try { childDirectory.Delete(false); } catch { report.errors++; }
                    continue;
                }
                FileInfo file = entry as FileInfo;
                if (file == null) continue;
                long length = 0L;
                try { length = Math.Max(0L, file.Length); } catch {}
                file.Delete();
                report.deletedBytes += length;
                report.deletedFiles++;
            } catch { report.errors++; }
        }
    }

    private void ClearCacheDirectory(string path, CacheDeleteReport report) {
        if (!IsCachePathInsideVamRoot(path)) { report.errors++; return; }
        try {
            if (!Directory.Exists(path)) return;
            DirectoryInfo directory = new DirectoryInfo(path);
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) { report.errors++; return; }
            DeleteCacheDirectoryContents(directory, report);
        } catch { report.errors++; }
    }

    private CacheDeleteReport ClearCacheFiles(bool allCaches) {
        CacheDeleteReport report = new CacheDeleteReport();
        List<string> directories = GetNonEssentialCacheDirectories();
        if (allCaches) directories.Add(Path.Combine(vamRoot, "Cache"));
        for (int i = 0; i < directories.Count; i++) ClearCacheDirectory(directories[i], report);
        try { Directory.CreateDirectory(thumbRoot); } catch { report.errors++; }
        return report;
    }

    private static string FormatCacheBytes(long bytes) {
        double value = Math.Max(0L, bytes);
        if (value >= 1024.0 * 1024.0 * 1024.0) return (value / (1024.0 * 1024.0 * 1024.0)).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
        if (value >= 1024.0 * 1024.0) return (value / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MiB";
        if (value >= 1024.0) return (value / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
        return ((long)value).ToString(CultureInfo.InvariantCulture) + " B";
    }

    private void UpdateCacheUsageUi() {
        if (cacheSizeText != null) {
            if (lastCacheUsage == null) cacheSizeText.text = cacheWorkerRunning ? "缓存大小：统计中..." : "缓存大小：尚未统计";
            else cacheSizeText.text = "非必要 " + FormatCacheBytes(lastCacheUsage.nonEssentialBytes) + "（" + lastCacheUsage.nonEssentialFiles + " 文件）  |  全部 " + FormatCacheBytes(lastCacheUsage.allBytes) + "（" + lastCacheUsage.allFiles + " 文件）";
        }
        if (clearNonEssentialCacheBtn != null) clearNonEssentialCacheBtn.interactable = !cacheWorkerRunning && lastCacheUsage != null;
        if (clearAllCacheBtn != null) clearAllCacheBtn.interactable = !cacheWorkerRunning && lastCacheUsage != null;
    }

    private void StartCacheUsageScan() {
        if (cacheWorkerRunning) return;
        cacheWorkerRunning = true;
        cacheWorkerResult = null;
        UpdateCacheUsageUi();
        Thread worker = new Thread(delegate() {
            CacheWorkerResult result = new CacheWorkerResult();
            result.operation = "scan";
            try { result.usage = MeasureCacheUsage(); }
            catch (Exception e) { result.error = e.Message; }
            cacheWorkerResult = result;
        });
        worker.IsBackground = true;
        worker.Name = "APL Cache Scan";
        worker.Start();
    }

    private bool CacheClearBlockedBySceneLoad() {
        SuperController sc = SuperController.singleton;
        return sceneLoadProfileActive || !string.IsNullOrEmpty(pendingScenePath) || (sc != null && sc.isLoading);
    }

    private void StartCacheClear(bool allCaches) {
        if (cacheWorkerRunning) { SetStatus("缓存任务正在运行，请稍候。", false); return; }
        if (scanning) { SetStatus("资源库正在扫描，暂不能清理缓存。", true); return; }
        if (CacheClearBlockedBySceneLoad()) { SetStatus("场景正在准备或加载，暂不能清理缓存。", true); return; }
        HideClearConfirm();
        StopThumbLoadCoroutine();
        StopScenePrewarm(true);
        CancelPendingSceneLoad();
        ClearPendingDeferredScene();
        ClearPreview();
        ClearListThumbs();
        cacheWorkerRunning = true;
        cacheWorkerResult = null;
        UpdateCacheUsageUi();
        SetStatus(allCaches ? "正在后台清除全部缓存..." : "正在后台清除非必要缓存...", true);
        Thread worker = new Thread(delegate() {
            CacheWorkerResult result = new CacheWorkerResult();
            result.operation = allCaches ? "clear-all" : "clear-non-essential";
            try {
                CacheDeleteReport report = ClearCacheFiles(allCaches);
                result.deletedBytes = report.deletedBytes;
                result.deletedFiles = report.deletedFiles;
                result.deleteErrors = report.errors;
                result.usage = MeasureCacheUsage();
            } catch (Exception e) { result.error = e.Message; }
            cacheWorkerResult = result;
        });
        worker.IsBackground = true;
        worker.Name = allCaches ? "APL Clear All Cache" : "APL Clear Nonessential Cache";
        worker.Start();
    }

    private void PollCacheWorkerResult() {
        CacheWorkerResult result = cacheWorkerResult;
        if (result == null) return;
        cacheWorkerResult = null;
        cacheWorkerRunning = false;
        if (result.usage != null) lastCacheUsage = result.usage;
        UpdateCacheUsageUi();
        if (result.operation == "scan") {
            if (!string.IsNullOrEmpty(result.error)) SetStatus("缓存大小统计失败：" + result.error, true);
            return;
        }
        materializedScriptRoots.Clear();
        if (!string.IsNullOrEmpty(result.error)) {
            SetStatus("缓存清理失败：" + result.error, true);
            return;
        }
        string label = result.operation == "clear-all" ? "全部缓存" : "非必要缓存";
        string message = "已清除" + label + "：释放 " + FormatCacheBytes(result.deletedBytes) + "，删除 " + result.deletedFiles + " 个文件，失败 " + result.deleteErrors + " 个";
        if (result.operation == "clear-all") message += "；建议重启 VaM 后继续使用";
        SetStatus(message, true);
        DebugLog("Cache clear complete. operation=" + result.operation + ", deletedBytes=" + result.deletedBytes + ", deletedFiles=" + result.deletedFiles + ", errors=" + result.deleteErrors);
    }

    private void ShowCacheClearConfirm(bool allCaches) {
        try {
            if (root == null || lastCacheUsage == null) return;
            if (cacheWorkerRunning) { SetStatus("缓存任务正在运行，请稍候。", false); return; }
            if (scanning) { SetStatus("资源库正在扫描，暂不能清理缓存。", true); return; }
            if (CacheClearBlockedBySceneLoad()) { SetStatus("场景正在准备或加载，暂不能清理缓存。", true); return; }
            if (confirmRoot != null) Destroy(confirmRoot);
            long bytes = allCaches ? lastCacheUsage.allBytes : lastCacheUsage.nonEssentialBytes;
            int files = allCaches ? lastCacheUsage.allFiles : lastCacheUsage.nonEssentialFiles;
            confirmRoot = new GameObject(allCaches ? "确认清除全部缓存" : "确认清除非必要缓存");
            confirmRoot.transform.SetParent(root.transform, false);
            Image overlay = confirmRoot.AddComponent<Image>();
            overlay.color = new Color(0f, 0f, 0f, 0.78f);
            RectTransform oRt = overlay.rectTransform;
            oRt.anchorMin = Vector2.zero; oRt.anchorMax = Vector2.one; oRt.offsetMin = Vector2.zero; oRt.offsetMax = Vector2.zero;
            GameObject box = new GameObject("确认框");
            box.transform.SetParent(confirmRoot.transform, false);
            Image boxImg = box.AddComponent<Image>();
            boxImg.color = colPanel;
            RectTransform boxRt = box.GetComponent<RectTransform>();
            boxRt.anchorMin = new Vector2(0.22f, 0.27f); boxRt.anchorMax = new Vector2(0.78f, 0.73f); boxRt.offsetMin = Vector2.zero; boxRt.offsetMax = Vector2.zero;
            Text title = MakeText(box.transform, "标题", allCaches ? "确认清除全部缓存？" : "确认清除非必要缓存？", 23, TextAnchor.MiddleCenter, colTextPrimary);
            RectTransform tRt = title.rectTransform;
            tRt.anchorMin = new Vector2(0.05f, 0.76f); tRt.anchorMax = new Vector2(0.95f, 0.94f); tRt.offsetMin = Vector2.zero; tRt.offsetMax = Vector2.zero;
            string scope = allCaches
                ? "包含 APL 可重建缓存和 VaM 纹理/PackageJSON 缓存。\n保留资源索引，不会删除 VAR、场景源文件、预设、配置或收藏。\n清理后首次场景加载会明显变慢，建议完成后重启 VaM。"
                : "包含 APL 缩略图、Timeline 派生文件及临时场景/脚本/预设。\n保留资源索引、VaM 纹理缓存、配置和收藏。\n相关内容会在下次使用时自动重建。";
            Text msg = MakeText(box.transform, "说明", scope + "\n\n预计释放：" + FormatCacheBytes(bytes) + " | 文件：" + files, 15, TextAnchor.UpperCenter, colTextSecondary);
            msg.horizontalOverflow = HorizontalWrapMode.Wrap;
            msg.verticalOverflow = VerticalWrapMode.Overflow;
            RectTransform mRt = msg.rectTransform;
            mRt.anchorMin = new Vector2(0.08f, 0.31f); mRt.anchorMax = new Vector2(0.92f, 0.75f); mRt.offsetMin = Vector2.zero; mRt.offsetMax = Vector2.zero;
            Button yes = MakeButton(box.transform, allCaches ? "确认全部清除" : "确认清除", 17, colDanger);
            RectTransform yRt = yes.GetComponent<RectTransform>();
            yRt.anchorMin = new Vector2(0.12f, 0.08f); yRt.anchorMax = new Vector2(0.45f, 0.25f); yRt.offsetMin = Vector2.zero; yRt.offsetMax = Vector2.zero;
            yes.onClick.AddListener(() => StartCacheClear(allCaches));
            Button no = MakeButton(box.transform, "取消", 17, colBtn);
            RectTransform nRt = no.GetComponent<RectTransform>();
            nRt.anchorMin = new Vector2(0.55f, 0.08f); nRt.anchorMax = new Vector2(0.88f, 0.25f); nRt.offsetMin = Vector2.zero; nRt.offsetMax = Vector2.zero;
            no.onClick.AddListener(() => HideClearConfirm());
        } catch (Exception e) { SetStatus("缓存确认框打开失败：" + e.Message, true); }
    }

    private void ShowClearConfirm(){ try{ if(root==null)return; if(confirmRoot!=null)Destroy(confirmRoot); int deletable,skipped; CountGeneratedLinks(out deletable,out skipped); confirmRoot=new GameObject("确认删除软链接"); confirmRoot.transform.SetParent(root.transform,false); Image overlay=confirmRoot.AddComponent<Image>(); overlay.color=new Color(0f,0f,0f,0.75f); RectTransform oRt=overlay.rectTransform; oRt.anchorMin=Vector2.zero; oRt.anchorMax=Vector2.one; oRt.offsetMin=Vector2.zero; oRt.offsetMax=Vector2.zero; GameObject box=new GameObject("确认框"); box.transform.SetParent(confirmRoot.transform,false); Image boxImg=box.AddComponent<Image>(); boxImg.color=colPanel; RectTransform boxRt=box.GetComponent<RectTransform>(); boxRt.anchorMin=new Vector2(0.25f,0.30f); boxRt.anchorMax=new Vector2(0.75f,0.70f); boxRt.offsetMin=Vector2.zero; boxRt.offsetMax=Vector2.zero; Text title=MakeText(box.transform,"标题","确认清除插件生成的软链接？",24,TextAnchor.MiddleCenter,colTextPrimary); RectTransform tRt=title.rectTransform; tRt.anchorMin=new Vector2(0.05f,0.75f); tRt.anchorMax=new Vector2(0.95f,0.95f); tRt.offsetMin=Vector2.zero; tRt.offsetMax=Vector2.zero; Text msg=MakeText(box.transform,"说明","将只删除 _AllPackagesLinkerLinks 目录里的 .var 链接项。\n不会删除 Allpackages 中的真实包，也不会碰其他包。\n\n可删除："+deletable+" 个 | 默认保留跳过："+skipped+" 个",16,TextAnchor.UpperCenter,colTextSecondary); RectTransform mRt=msg.rectTransform; mRt.anchorMin=new Vector2(0.08f,0.35f); mRt.anchorMax=new Vector2(0.92f,0.74f); mRt.offsetMin=Vector2.zero; mRt.offsetMax=Vector2.zero; Button yes=MakeButton(box.transform,"确认删除",18,new Color(0.600f,0.200f,0.200f,0.95f)); RectTransform yRt=yes.GetComponent<RectTransform>(); yRt.anchorMin=new Vector2(0.12f,0.08f); yRt.anchorMax=new Vector2(0.45f,0.25f); yRt.offsetMin=Vector2.zero; yRt.offsetMax=Vector2.zero; yes.onClick.AddListener(()=>{HideClearConfirm();ClearLinks();RefreshList();}); Button no=MakeButton(box.transform,"取消",18,colBtn); RectTransform nRt=no.GetComponent<RectTransform>(); nRt.anchorMin=new Vector2(0.55f,0.08f); nRt.anchorMax=new Vector2(0.88f,0.25f); nRt.offsetMin=Vector2.zero; nRt.offsetMax=Vector2.zero; no.onClick.AddListener(()=>HideClearConfirm()); SetStatus("请确认是否删除插件生成的软链接。",false); }catch(Exception e){SetStatus("确认框打开失败："+e.Message,true);} }
    private void HideClearConfirm(){ try{ if(confirmRoot!=null)Destroy(confirmRoot); }catch{} confirmRoot=null; }
    private void CountGeneratedLinks(out int deletable,out int skippedDefault){ deletable=0; skippedDefault=0; try{ if(!Directory.Exists(linkRoot))return; string basePath=Path.GetFullPath(linkRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar; string[] files=Directory.GetFiles(linkRoot,"*.var",SearchOption.AllDirectories); foreach(string f in files){ string full=Path.GetFullPath(f); if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))continue; string uid=Path.GetFileNameWithoutExtension(f); if(defaultUids.Contains(uid))skippedDefault++; else deletable++; } }catch{} }
    private void ClearLinks(){ try{ if(!Directory.Exists(linkRoot)){SetStatus("没有可清理的插件生成链接。",true);return;} int deleted=0,skipped=0,errors=0; string basePath=Path.GetFullPath(linkRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar; string[] files=Directory.GetFiles(linkRoot,"*.var",SearchOption.AllDirectories); foreach(string f in files){ string full=Path.GetFullPath(f); if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))continue; string uid=Path.GetFileNameWithoutExtension(f); if(defaultUids.Contains(uid)){skipped++;continue;} try{File.Delete(full);deleted++;}catch(Exception e){errors++;DebugLog("Delete generated link failed "+full+": "+e.Message);} } RemoveEmptyDirs(linkRoot); SetStatus("已清除插件生成软链接：删除="+deleted+"，默认保留跳过="+skipped+"，失败="+errors,true); RefreshVam(); }catch(Exception e){SetStatus("清除失败："+e.Message,true);} }
    private int ClearGeneratedLinksForSceneLoad(){ int deleted=0,skipped=0,errors=0; try{ if(!Directory.Exists(linkRoot))return 0; string basePath=Path.GetFullPath(linkRoot).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)+Path.DirectorySeparatorChar; string[] files=Directory.GetFiles(linkRoot,"*.var",SearchOption.AllDirectories); foreach(string f in files){ string full=Path.GetFullPath(f); if(!full.StartsWith(basePath,StringComparison.OrdinalIgnoreCase))continue; string uid=Path.GetFileNameWithoutExtension(f); if(defaultUids.Contains(uid)){skipped++;continue;} try{File.Delete(full);deleted++;}catch(Exception e){errors++;DebugLog("Auto clean generated link failed "+full+": "+e.Message);} } if(deleted>0)RemoveEmptyDirs(linkRoot); if(deleted>0||skipped>0||errors>0)DebugLog("Auto clean before scene load: deleted="+deleted+", defaultSkipped="+skipped+", errors="+errors); }catch(Exception e){ DebugLog("Auto clean before scene load failed: "+e.Message); } return deleted; }
    private void RemoveEmptyDirs(string rootDir){ try{ string[] dirs=Directory.GetDirectories(rootDir,"*",SearchOption.AllDirectories); Array.Sort(dirs); for(int i=dirs.Length-1;i>=0;i--){ try{ if(Directory.GetFiles(dirs[i]).Length==0 && Directory.GetDirectories(dirs[i]).Length==0) Directory.Delete(dirs[i]); }catch{} } }catch{} }
    private void RefreshVam(){
        Stopwatch sw=Stopwatch.StartNew();
        int cleaned=0;
        try{cleaned=CleanBrokenGeneratedLinks();}catch(Exception e){DebugLog("RefreshVam broken-link cleanup failed: "+e.Message);}
        try{
            // VaM 1.22 SuperController.RescanPackages() is only a wrapper around
            // FileManager.Refresh(), so calling both performs the same package scan twice.
            FileManager.Refresh();
        }catch(Exception e){DebugLog("RefreshVam FileManager.Refresh failed: "+e.Message);}
        ScanAddonLightweight();
        sw.Stop();
        DebugLog("RefreshVam completed. ms="+sw.Elapsed.TotalMilliseconds.ToString("0")+", cleaned="+cleaned+", addon="+addonExact.Count);
    }

    private void DeleteSelectedPackage() {
        if (selected == null) { SetStatus("未选择包。", false); return; }
        try {
            string path = selected.fullPath;
            if (!File.Exists(path)) { SetStatus("文件不存在：" + path, true); return; }
            // Safety: only delete from Allpackages directory
            string fullAll = Path.GetFullPath(allRoot).TrimEnd('\\', '/') + "\\";
            string fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(fullAll, StringComparison.OrdinalIgnoreCase)) {
                SetStatus("安全限制：只能删除 Allpackages 目录中的包。", true);
                return;
            }
            File.Delete(path);
            // Remove from index
            all.Remove(selected);
            allExact.Remove(selected.uid);
            string latestKey = Group(selected.uid) + ".latest";
            PackageLite lp; if (allLatest.TryGetValue(latestKey, out lp) && lp == selected) allLatest.Remove(latestKey);
            SetStatus("已删除包：" + selected.uid, true);
            selected = null;
            SaveCache(all);
            RefreshList();
        } catch (Exception e) {
            SetStatus("删除失败：" + e.Message, true);
            DebugLog("DeleteSelectedPackage FAILED: " + e.ToString());
        }
    }


    private void BuildSettingsDrawer() {
        settingsBackdropRoot = new GameObject("SettingsBackdrop");
        settingsBackdropRoot.transform.SetParent(root.transform, false);
        Image bd = settingsBackdropRoot.AddComponent<Image>();
        bd.color = new Color(0f, 0f, 0f, 0.45f);
        RectTransform bdRt = bd.rectTransform;
        bdRt.anchorMin = Vector2.zero; bdRt.anchorMax = Vector2.one;
        bdRt.offsetMin = Vector2.zero; bdRt.offsetMax = Vector2.zero;
        Button bdBtn = settingsBackdropRoot.AddComponent<Button>();
        bdBtn.targetGraphic = bd;
        bdBtn.onClick.AddListener(() => SetSettingsDrawer(false));

        settingsDrawerRoot = new GameObject("SettingsDrawer");
        settingsDrawerRoot.transform.SetParent(root.transform, false);
        Image drawerBg = settingsDrawerRoot.AddComponent<Image>();
        drawerBg.color = colPanel;
        RectTransform dr = settingsDrawerRoot.GetComponent<RectTransform>();
        // 更宽的设置面板，避免选项文字被裁切
        dr.anchorMin = new Vector2(isVRMode ? 0.48f : 0.52f, 0.06f);
        dr.anchorMax = new Vector2(0.985f, 0.94f);
        dr.offsetMin = Vector2.zero; dr.offsetMax = Vector2.zero;

        ScrollRect scroll = settingsDrawerRoot.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.scrollSensitivity = 28f;
        GameObject vp = new GameObject("Viewport");
        vp.transform.SetParent(settingsDrawerRoot.transform, false);
        RectTransform vrt = vp.AddComponent<RectTransform>();
        StretchFull(vrt, 12, 12, 12, 12);
        vp.AddComponent<Image>().color = new Color(0,0,0,0.01f);
        vp.AddComponent<Mask>().showMaskGraphic = false;
        GameObject content = new GameObject("Content");
        content.transform.SetParent(vp.transform, false);
        RectTransform crt = content.AddComponent<RectTransform>();
        crt.anchorMin = new Vector2(0, 1); crt.anchorMax = new Vector2(1, 1);
        crt.pivot = new Vector2(0.5f, 1); crt.offsetMin = Vector2.zero; crt.offsetMax = Vector2.zero;
        VerticalLayoutGroup vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = 10; vlg.padding = new RectOffset(10, 10, 10, 16);
        vlg.childControlWidth = true; vlg.childControlHeight = false;
        vlg.childForceExpandWidth = true; vlg.childForceExpandHeight = false;
        content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scroll.viewport = vrt; scroll.content = crt;

        Text title = MakeText(content.transform, "Title", "设置与维护", 20, TextAnchor.MiddleLeft, colTextPrimary);
        SetFixedHeight(title.gameObject, 36f);
        Text tip = MakeText(content.transform, "Tip", "下面这些是全局设置，不在收藏/脚本列表里。", 13, TextAnchor.MiddleLeft, colTextDim);
        SetFixedHeight(tip.gameObject, 24f);

        Text g1 = MakeText(content.transform, "G1", "启动与打开", 16, TextAnchor.MiddleLeft, colAccent);
        SetFixedHeight(g1.gameObject, 28f);
        // 最显眼的首项：插件加载后自动打开
        Toggle autoOpenLoadTg = MakeToggle(content.transform, "插件加载后自动打开本界面", autoOpenPanelOnPluginLoad);
        SetFixedHeight(autoOpenLoadTg.gameObject, isVRMode ? 48f : 42f);
        autoOpenLoadTg.onValueChanged.AddListener((bool v) => { autoOpenPanelOnPluginLoad = v; SaveConfig(); SetStatus("插件加载后自动打开界面：" + (v ? "开" : "关"), true); });
        Text autoOpenHint = MakeText(content.transform, "AutoOpenHint", "开启后：BepInEx 加载本插件约 2 秒自动弹出。VR 下会开世界空间面板（头显可见），桌面为 Overlay。", 12, TextAnchor.UpperLeft, colTextSecondary);
        autoOpenHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        autoOpenHint.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(autoOpenHint.gameObject, 48f);

        Toggle autoOpenTargetPluginTg = MakeToggle(content.transform, "脚本加载到原子后自动打开该原子的插件面板", autoOpenTargetAtomPluginPanel);
        SetFixedHeight(autoOpenTargetPluginTg.gameObject, isVRMode ? 48f : 42f);
        autoOpenTargetPluginTg.onValueChanged.AddListener((bool v) => { autoOpenTargetAtomPluginPanel = v; SaveConfig(); SetStatus("脚本加载后打开目标原子插件面板：" + (v ? "开" : "关"), true); });
        Text autoOpenTargetHint = MakeText(content.transform, "AutoOpenTargetHint", "此项与“插件加载后自动打开本界面”不同：它会在脚本完成加载后选中目标原子，打开 VaM 原生编辑界面和 Plugins 面板，并显示脚本 UI。", 12, TextAnchor.UpperLeft, colTextSecondary);
        autoOpenTargetHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        autoOpenTargetHint.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(autoOpenTargetHint.gameObject, 52f);

        Toggle autoOpenTg = MakeToggle(content.transform, "仅编辑模式自动打开（兼容旧选项）", autoOpenPanelInEditMode);
        SetFixedHeight(autoOpenTg.gameObject, isVRMode ? 44f : 40f);
        autoOpenTg.onValueChanged.AddListener((bool v) => { autoOpenPanelInEditMode = v; SaveConfig(); SetStatus("编辑模式自动打开：" + (v ? "开" : "关"), true); });
        Toggle autoAllowPluginTg = MakeToggle(content.transform, "总是允许加载所有插件", autoAllowAllPlugins);
        SetFixedHeight(autoAllowPluginTg.gameObject, isVRMode ? 44f : 40f);
        autoAllowPluginTg.onValueChanged.AddListener((bool v) => { autoAllowAllPlugins = v; SaveConfig(); SetStatus("总是允许加载所有插件：" + (v ? "开" : "关"), true); if(v) Invoke("AutoAllowAllPendingPluginPackages", 0.2f); });
        Button rescanSet = MakeButton(content.transform, scanning ? "扫描中..." : "重新扫描资源库", 15, colAccentDim);
        SetFixedHeight(rescanSet.gameObject, isVRMode ? 48f : 42f);
        rescanTopBtn = rescanSet;
        rescanSet.onClick.AddListener(() => {
            if (scanning) { SetStatus("正在扫描中，请稍候...", false); return; }
            ScanPackages(); RefreshList(); SetStatus("已触发重新扫描", true);
        });

        Text g2 = MakeText(content.transform, "G2", "扫描与加载", 15, TextAnchor.MiddleLeft, colAccent);
        SetFixedHeight(g2.gameObject, 24f);
        Toggle startupScanTg = MakeToggle(content.transform, "启动时扫描全库", scanAllPackagesOnStartup);
        SetFixedHeight(startupScanTg.gameObject, 34f);
        startupScanTg.onValueChanged.AddListener((bool v) => { scanAllPackagesOnStartup = v; SaveConfig(); SetStatus("启动时扫描全库：" + (v ? "开" : "关"), true); });
        Toggle autoCleanLinksTg = MakeToggle(content.transform, "加载场景前清理旧链接", autoCleanLinksBeforeSceneLoad);
        SetFixedHeight(autoCleanLinksTg.gameObject, 34f);
        autoCleanLinksTg.onValueChanged.AddListener((bool v) => { autoCleanLinksBeforeSceneLoad = v; SaveConfig(); SetStatus("加载场景前清理旧链接：" + (v ? "开" : "关"), true); });
        Toggle scenePrewarmTg = MakeToggle(content.transform, "选中场景后预热主人物皮肤", sceneTexturePrewarmEnabled);
        SetFixedHeight(scenePrewarmTg.gameObject, isVRMode ? 44f : 38f);
        scenePrewarmTg.onValueChanged.AddListener((bool v) => {
            sceneTexturePrewarmEnabled = v;
            SaveConfig();
            if (v) StartSelectedScenePrewarm(); else StopScenePrewarm(true);
            SetStatus("场景皮肤预热：" + (v ? "开" : "关"), true);
        });
        Toggle lazyCuaTg = MakeToggle(content.transform, "初始关闭的 CUA 按需加载", lazyDisabledCuaEnabled);
        SetFixedHeight(lazyCuaTg.gameObject, isVRMode ? 44f : 38f);
        lazyCuaTg.onValueChanged.AddListener((bool v) => { SetLazyDisabledCuaEnabled(v); });

        Text textureFinishTitle = MakeText(content.transform, "TextureFinishTitle", "纹理收尾（仅加载时）", 14, TextAnchor.MiddleLeft, colTextSecondary);
        SetFixedHeight(textureFinishTitle.gameObject, 24f);
        GameObject textureFinishRow = CreateRow(content.transform, "TextureFinishGear", isVRMode ? 46f : 40f, 6, true);
        Button textureVanillaBtn = MakeButton(textureFinishRow.transform, "原版 4", 12, colBtn);
        Button textureBalancedBtn = MakeButton(textureFinishRow.transform, "均衡 8", 12, colBtn);
        Button textureFastBtn = MakeButton(textureFinishRow.transform, "高速 12", 12, colBtn);
        Button textureExtremeBtn = MakeButton(textureFinishRow.transform, "极限 16", 12, colBtn);
        SetFlexibleItem(textureVanillaBtn.gameObject, 0f, 1f);
        SetFlexibleItem(textureBalancedBtn.gameObject, 0f, 1f);
        SetFlexibleItem(textureFastBtn.gameObject, 0f, 1f);
        SetFlexibleItem(textureExtremeBtn.gameObject, 0f, 1f);
        UiAction refreshTextureGearColors = () => {
            SetModeButtonColor(textureVanillaBtn, textureFinishGear == 0);
            SetModeButtonColor(textureBalancedBtn, textureFinishGear == 1);
            SetModeButtonColor(textureFastBtn, textureFinishGear == 2);
            SetModeButtonColor(textureExtremeBtn, textureFinishGear == 3);
        };
        textureVanillaBtn.onClick.AddListener(() => { SetTextureFinishGear(0); refreshTextureGearColors(); });
        textureBalancedBtn.onClick.AddListener(() => { SetTextureFinishGear(1); refreshTextureGearColors(); });
        textureFastBtn.onClick.AddListener(() => { SetTextureFinishGear(2); refreshTextureGearColors(); });
        textureExtremeBtn.onClick.AddListener(() => { SetTextureFinishGear(3); refreshTextureGearColors(); });
        refreshTextureGearColors();

        Text assetCallbackTitle = MakeText(content.transform, "AssetCallbackTitle", "CUA AssetBundle（重启生效）", 14, TextAnchor.MiddleLeft, colTextSecondary);
        SetFixedHeight(assetCallbackTitle.gameObject, 24f);
        GameObject assetCallbackRow = CreateRow(content.transform, "AssetCallbackGear", isVRMode ? 46f : 40f, 6, true);
        Button assetOrderedBtn = MakeButton(assetCallbackRow.transform, "顺序 8", 12, colBtn);
        Button assetBalancedBtn = MakeButton(assetCallbackRow.transform, "均衡 8", 12, colBtn);
        Button assetFastBtn = MakeButton(assetCallbackRow.transform, "高速 12", 12, colBtn);
        Button assetExtremeBtn = MakeButton(assetCallbackRow.transform, "极限 16", 12, colBtn);
        SetFlexibleItem(assetOrderedBtn.gameObject, 0f, 1f);
        SetFlexibleItem(assetBalancedBtn.gameObject, 0f, 1f);
        SetFlexibleItem(assetFastBtn.gameObject, 0f, 1f);
        SetFlexibleItem(assetExtremeBtn.gameObject, 0f, 1f);
        UiAction refreshAssetCallbackGearColors = () => {
            SetModeButtonColor(assetOrderedBtn, assetCallbackGear == 0);
            SetModeButtonColor(assetBalancedBtn, assetCallbackGear == 1);
            SetModeButtonColor(assetFastBtn, assetCallbackGear == 2);
            SetModeButtonColor(assetExtremeBtn, assetCallbackGear == 3);
        };
        assetOrderedBtn.onClick.AddListener(() => { SetAssetCallbackGear(0); refreshAssetCallbackGearColors(); });
        assetBalancedBtn.onClick.AddListener(() => { SetAssetCallbackGear(1); refreshAssetCallbackGearColors(); });
        assetFastBtn.onClick.AddListener(() => { SetAssetCallbackGear(2); refreshAssetCallbackGearColors(); });
        assetExtremeBtn.onClick.AddListener(() => { SetAssetCallbackGear(3); refreshAssetCallbackGearColors(); });
        refreshAssetCallbackGearColors();

        // VR 镜头模式：桌面/VR 设置都可配，不依赖当前面板模式
        Text gVrRot = MakeText(content.transform, "GVrRot", "VR 镜头模式", 15, TextAnchor.MiddleLeft, colAccent);
        SetFixedHeight(gVrRot.gameObject, 24f);
        Toggle vrRotTg = MakeToggle(content.transform, "启用镜头模式（左摇杆按下切换）", vrRotationEnabled);
        SetFixedHeight(vrRotTg.gameObject, isVRMode ? 48f : 42f);
        vrRotTg.onValueChanged.AddListener((bool v) => {
            vrRotationEnabled = v;
            if (!v) ExitVrRotationMode("settings-off");
            SaveConfig();
            SetStatus("镜头模式：" + (v ? "开" : "关"), true);
        });
        Text vrRotHint = MakeText(content.transform, "VrRotHint", "左移动摇杆按一下进入/退出。开启后：左右推杆=水平转向，前后推杆=升高/降低视角。", 12, TextAnchor.UpperLeft, colTextSecondary);
        vrRotHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        vrRotHint.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(vrRotHint.gameObject, 48f);
        Toggle vrInvTg = MakeToggle(content.transform, "反转左右转向", vrRotationInvert);
        SetFixedHeight(vrInvTg.gameObject, isVRMode ? 44f : 38f);
        vrInvTg.onValueChanged.AddListener((bool v) => { vrRotationInvert = v; SaveConfig(); SetStatus("左右转向反转：" + (v ? "开" : "关"), true); });
        Toggle vrHInvTg = MakeToggle(content.transform, "反转升降方向", vrHeightInvert);
        SetFixedHeight(vrHInvTg.gameObject, isVRMode ? 44f : 38f);
        vrHInvTg.onValueChanged.AddListener((bool v) => { vrHeightInvert = v; SaveConfig(); SetStatus("升降反转：" + (v ? "开" : "关"), true); });
        GameObject sensRow = CreateRow(content.transform, "VrSens", isVRMode ? 42f : 36f, 6, true);
        Button sensMinus = MakeButton(sensRow.transform, "转向慢", 13, colBtn); SetFlexibleItem(sensMinus.gameObject, 0f, 1f);
        sensMinus.onClick.AddListener(() => { vrRotationSensitivity = Mathf.Clamp(vrRotationSensitivity - 10f, 10f, 180f); SaveConfig(); SetStatus("转向灵敏度=" + vrRotationSensitivity.ToString("0") + "°/s", false); });
        Button sensPlus = MakeButton(sensRow.transform, "转向快", 13, colBtn); SetFlexibleItem(sensPlus.gameObject, 0f, 1f);
        sensPlus.onClick.AddListener(() => { vrRotationSensitivity = Mathf.Clamp(vrRotationSensitivity + 10f, 10f, 180f); SaveConfig(); SetStatus("转向灵敏度=" + vrRotationSensitivity.ToString("0") + "°/s", false); });
        GameObject hRow = CreateRow(content.transform, "VrHeight", isVRMode ? 42f : 36f, 6, true);
        Button hMinus = MakeButton(hRow.transform, "升降慢", 13, colBtn); SetFlexibleItem(hMinus.gameObject, 0f, 1f);
        hMinus.onClick.AddListener(() => { vrHeightSpeed = Mathf.Clamp(vrHeightSpeed - 0.15f, 0.10f, 3.00f); SaveConfig(); SetStatus("升降速度=" + vrHeightSpeed.ToString("0.00") + " m/s", false); });
        Button hPlus = MakeButton(hRow.transform, "升降快", 13, colBtn); SetFlexibleItem(hPlus.gameObject, 0f, 1f);
        hPlus.onClick.AddListener(() => { vrHeightSpeed = Mathf.Clamp(vrHeightSpeed + 0.15f, 0.10f, 3.00f); SaveConfig(); SetStatus("升降速度=" + vrHeightSpeed.ToString("0.00") + " m/s", false); });
        GameObject snapRow = CreateRow(content.transform, "VrSnap", isVRMode ? 42f : 36f, 6, true);
        Button snapCont = MakeButton(snapRow.transform, "连续转向", 12, colBtn); SetFlexibleItem(snapCont.gameObject, 0f, 1f);
        snapCont.onClick.AddListener(() => { vrRotationSnapAngle = 0f; SaveConfig(); SetStatus("镜头转向：连续", false); });
        Button snap30 = MakeButton(snapRow.transform, "舒适30°", 12, colBtn); SetFlexibleItem(snap30.gameObject, 0f, 1f);
        snap30.onClick.AddListener(() => { vrRotationSnapAngle = 30f; SaveConfig(); SetStatus("镜头转向：舒适 30°", false); });
        Button snap45 = MakeButton(snapRow.transform, "舒适45°", 12, colBtn); SetFlexibleItem(snap45.gameObject, 0f, 1f);
        snap45.onClick.AddListener(() => { vrRotationSnapAngle = 45f; SaveConfig(); SetStatus("镜头转向：舒适 45°", false); });

        if (isVRMode) {
            Text g3 = MakeText(content.transform, "G3", "VR 界面", 15, TextAnchor.MiddleLeft, colAccent);
            SetFixedHeight(g3.gameObject, 24f);
            GameObject vrRow1 = CreateRow(content.transform, "VrScale", 36f, 6, true);
            Button sMinus = MakeButton(vrRow1.transform, "缩放-", 13, colBtn); SetFlexibleItem(sMinus.gameObject, 0f, 1f); sMinus.onClick.AddListener(() => ChangeUiScale(0.90f));
            Button sPlus = MakeButton(vrRow1.transform, "缩放+", 13, colBtn); SetFlexibleItem(sPlus.gameObject, 0f, 1f); sPlus.onClick.AddListener(() => ChangeUiScale(1.10f));
            GameObject vrRow2 = CreateRow(content.transform, "VrDist", 36f, 6, true);
            Button dMinus = MakeButton(vrRow2.transform, "近一点", 13, colBtn); SetFlexibleItem(dMinus.gameObject, 0f, 1f); dMinus.onClick.AddListener(() => ChangeUiDistance(-0.10f));
            Button dPlus = MakeButton(vrRow2.transform, "远一点", 13, colBtn); SetFlexibleItem(dPlus.gameObject, 0f, 1f); dPlus.onClick.AddListener(() => ChangeUiDistance(0.10f));
            GameObject vrRow3 = CreateRow(content.transform, "VrY", 36f, 6, true);
            Button yUp = MakeButton(vrRow3.transform, "上移", 13, colBtn); SetFlexibleItem(yUp.gameObject, 0f, 1f); yUp.onClick.AddListener(() => ChangeUiYOffset(0.05f));
            Button yDn = MakeButton(vrRow3.transform, "下移", 13, colBtn); SetFlexibleItem(yDn.gameObject, 0f, 1f); yDn.onClick.AddListener(() => ChangeUiYOffset(-0.05f));
            GameObject vrRow4 = CreateRow(content.transform, "VrRecenter", 36f, 6, true);
            Button recenter = MakeButton(vrRow4.transform, "重新居中", 13, colAccentDim); SetFlexibleItem(recenter.gameObject, 0f, 1f); recenter.onClick.AddListener(() => RecenterVrCanvas());
        }

        Text g4 = MakeText(content.transform, "G4", "缓存", 15, TextAnchor.MiddleLeft, colAccent);
        SetFixedHeight(g4.gameObject, 24f);
        cacheSizeText = MakeText(content.transform, "CacheSize", "缓存大小：统计中...", 13, TextAnchor.MiddleLeft, colTextSecondary);
        cacheSizeText.horizontalOverflow = HorizontalWrapMode.Wrap;
        cacheSizeText.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(cacheSizeText.gameObject, isVRMode ? 48f : 42f);
        GameObject cacheRow = CreateRow(content.transform, "CacheActions", isVRMode ? 48f : 42f, 8, true);
        clearNonEssentialCacheBtn = MakeButton(cacheRow.transform, "清除非必要", 14, colBtn);
        SetFlexibleItem(clearNonEssentialCacheBtn.gameObject, 0f, 1f);
        clearNonEssentialCacheBtn.onClick.AddListener(() => ShowCacheClearConfirm(false));
        clearAllCacheBtn = MakeButton(cacheRow.transform, "清除全部", 14, colDanger);
        SetFlexibleItem(clearAllCacheBtn.gameObject, 0f, 1f);
        clearAllCacheBtn.onClick.AddListener(() => ShowCacheClearConfirm(true));
        Text cacheHint = MakeText(content.transform, "CacheHint", "非必要：APL 缩略图、Timeline 派生和临时文件。全部：另含 VaM 纹理缓存。资源索引、配置、收藏、VAR 和场景源文件始终保留。", 12, TextAnchor.UpperLeft, colTextDim);
        cacheHint.horizontalOverflow = HorizontalWrapMode.Wrap;
        cacheHint.verticalOverflow = VerticalWrapMode.Overflow;
        SetFixedHeight(cacheHint.gameObject, 58f);
        UpdateCacheUsageUi();
        if (lastCacheUsage == null && !cacheWorkerRunning) StartCacheUsageScan();

        Text g5 = MakeText(content.transform, "G5", "维护（危险）", 15, TextAnchor.MiddleLeft, new Color(0.85f, 0.30f, 0.30f, 1f));
        SetFixedHeight(g5.gameObject, 24f);
        dangerRowRoot = CreateRow(content.transform, "DangerRow", 42f, 8, true);
        Button clear = MakeButton(dangerRowRoot.transform, "清除生成链接", 14, colDanger);
        SetFlexibleItem(clear.gameObject, 0f, 1f);
        clear.onClick.AddListener(() => ShowClearConfirm());
        Button deleteBtn = MakeButton(dangerRowRoot.transform, "删除真实包", 14, colDanger);
        SetFlexibleItem(deleteBtn.gameObject, 0f, 1f);
        deleteBtn.onClick.AddListener(() => DeleteSelectedPackage());

        Button closeSet = MakeButton(content.transform, "关闭设置", 15, colBtn);
        SetFixedHeight(closeSet.gameObject, 40f);
        closeSet.onClick.AddListener(() => SetSettingsDrawer(false));

        SetSettingsDrawer(false);
    }

    private void ToggleSettingsDrawer() { SetSettingsDrawer(!settingsDrawerOpen); }
    private void SetSettingsDrawer(bool open) {
        settingsDrawerOpen = open;
        if (settingsDrawerRoot != null) settingsDrawerRoot.SetActive(open);
        if (settingsBackdropRoot != null) settingsBackdropRoot.SetActive(open);
    }

    private void UpdateSearchPlaceholder() {
        if (searchPlaceholderText == null) return;
        if (activeCat == "Scenes") searchPlaceholderText.text = "搜索场景名、包名或作者...";
        else if (activeCat == "Presets") searchPlaceholderText.text = "搜索预设名、路径或包名...";
        else if (activeCat == "Favorites") searchPlaceholderText.text = "搜索收藏项...";
        else if (activeCat == "Scripts") searchPlaceholderText.text = "搜索脚本包名...";
        else searchPlaceholderText.text = "搜索包名、描述或作者...";
    }

    private void UpdateResultToolbar(string section, int total, int start, int end, int maxPage) {
        if (resultCountText != null) {
            string range = total > 0 ? ((start + 1) + "-" + end + "/" + total) : "0";
            resultCountText.text = section + " · " + total + " 项 · " + range + " · 库 " + all.Count + " 包";
        }
        if (pageInfoText != null) pageInfoText.text = "第 " + (page + 1) + "/" + (maxPage + 1) + " 页 · 每页 " + pageSize;
    }

    private void ShowEmptyState(bool show) {
        if (emptyStateRoot == null) return;
        emptyStateRoot.SetActive(show);
        if (!show) return;
        Text t = emptyStateRoot.GetComponentInChildren<Text>();
        if (t == null) return;
        if (scanning) t.text = "正在扫描 / 建立索引...";
        else if (!string.IsNullOrEmpty(searchQuery)) t.text = "没有搜索结果";
        else t.text = "没有内容";
    }

    private void ClearSelectionKeepPreview(bool clearPreview) {
        LeaveSceneSelection();
        selected = null; selectedPreset = null; selectedVarPreset = null; selectedSceneItem = null; selectedWearableItem = null;
        if (clearPreview) ClearPreview();
        if (details != null) {
            details.text = "请从左侧选择一个资源后执行操作。";
            details.color = colTextDim;
        }
        UpdateInspectorVisibility();
    }

    private bool ContainsPackageInList(List<PackageLite> packages, string uid) {
        if (string.IsNullOrEmpty(uid) || packages == null) return false;
        for (int i = 0; i < packages.Count; i++) if (string.Equals(packages[i].uid, uid, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private bool ContainsLocalPreset(List<PresetLite> list, PresetLite pr) {
        if (pr == null || list == null) return false;
        for (int i = 0; i < list.Count; i++) if (string.Equals(list[i].fullPath, pr.fullPath, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    private bool ContainsVarPreset(List<VarPresetLite> list, VarPresetLite vp) {
        if (vp == null || vp.package == null || list == null) return false;
        string key = vp.package.uid + "|" + vp.entryPath;
        for (int i = 0; i < list.Count; i++) {
            if (list[i] == null || list[i].package == null) continue;
            if (string.Equals(list[i].package.uid + "|" + list[i].entryPath, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private bool ContainsScene(List<SceneLite> list, SceneLite si) {
        if (si == null || si.package == null || list == null) return false;
        string key = si.package.uid + "|" + si.entryPath;
        for (int i = 0; i < list.Count; i++) {
            if (list[i] == null || list[i].package == null) continue;
            if (string.Equals(list[i].package.uid + "|" + list[i].entryPath, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
    private bool ContainsWearable(List<WearableLite> list, WearableLite w) {
        if (w == null || w.package == null || list == null) return false;
        string key = w.package.uid + "|" + w.entryPath;
        for (int i = 0; i < list.Count; i++) {
            if (list[i] == null || list[i].package == null) continue;
            if (string.Equals(list[i].package.uid + "|" + list[i].entryPath, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private void UpdateInspectorVisibility() {
        bool hasAny = selected != null || selectedPreset != null || selectedVarPreset != null || selectedSceneItem != null || selectedWearableItem != null;
        bool isScene = selectedSceneItem != null;
        bool isLocalPreset = selectedPreset != null;
        bool isVarPreset = selectedVarPreset != null;
        bool isPreset = isLocalPreset || isVarPreset;
        bool isWearable = selectedWearableItem != null;
        bool isScript = selected != null && !isScene && !isPreset && !isWearable && (selected.cats.Contains("Scripts") || selected.cats.Contains("Plugins"));
        bool isPackage = selected != null && !isScene && !isPreset && !isWearable && !isScript;

        if (details != null && !hasAny) {
            details.text = "请从左侧选择一个资源后执行操作。";
            details.color = colTextDim;
        }

        SetGoActive(atomRowRoot, isPreset || isScript || isWearable);
        SetGoActive(presetOptionsRoot, isPreset);
        SetGoActive(presetModeRoot, false); // 快捷模式已移除
        SetGoActive(sceneModeRoot, isScene);
        SetGoActive(scenePersonRoot, isScene && selectedSceneAnalysis != null && selectedSceneAnalysis.personIds.Count > 0);
        bool canLoadScene = isScene || (selected != null && selected.firstScene != "" && activeCat == "Scenes" && selectedSceneItem == null);
        bool hasDeferredScene = !string.IsNullOrEmpty(pendingDeferredScenePath) && pendingDeferredAtomCount > 0;
        SetGoActive(sceneActionRoot, canLoadScene || hasDeferredScene);
        SetGoActive(presetActionRoot, isPreset);
        SetGoActive(moreActionsRoot, isScript);
        SetGoActive(linkActionRoot, hasAny);
        SetGoActive(hubRowRoot, selected != null || isScene || isVarPreset || isWearable);
        SetGoActive(hubDownloadRoot, selected != null || isScene || isVarPreset);
        SetGoActive(progressSectionRoot, missingDepsDownloadRunning || downloadProgressValue > 0.001f);

        if (loadSceneBtn != null) {
            var img = loadSceneBtn.GetComponent<Image>();
            if (img != null) img.color = hasAny && isScene ? colAccent : colBtn;
            loadSceneBtn.interactable = canLoadScene;
            loadSceneBtn.gameObject.SetActive(canLoadScene);
        }
        if (loadDeferredSceneBtn != null) loadDeferredSceneBtn.gameObject.SetActive(hasDeferredScene);
        UpdateSceneLoadModeUI();
        if (applyPresetBtn != null) applyPresetBtn.interactable = isPreset;
        if (loadScriptBtn != null) loadScriptBtn.interactable = isScript;
        if (linkOnlyBtn != null) linkOnlyBtn.interactable = selected != null;
        if (defaultKeepBtn != null) defaultKeepBtn.interactable = selected != null;
        if (favToggleBtn != null) favToggleBtn.interactable = hasAny;
        if (rescanTopBtn != null) {
            Text rt = rescanTopBtn.GetComponentInChildren<Text>();
            if (rt != null) rt.text = scanning ? "扫描中..." : "重新扫描";
            rescanTopBtn.interactable = !scanning;
        }
    }

    private void SetGoActive(GameObject go, bool active) {
        if (go != null && go.activeSelf != active) go.SetActive(active);
    }


    private Text Text(Transform parent,string name,string text,int size,TextAnchor align){ return MakeText(parent,name,text,size,align,colTextPrimary); }
    private Text MakeText(Transform parent,string name,string text,int size,TextAnchor align,Color color){ GameObject go=new GameObject(name); go.transform.SetParent(parent,false); Text t=go.AddComponent<Text>(); t.font=font; t.fontSize=size; t.color=color; t.alignment=align; t.text=text; t.horizontalOverflow=HorizontalWrapMode.Wrap; t.verticalOverflow=VerticalWrapMode.Truncate; return t; }
    private Image Image(Transform parent,string name,Color color){ return MakeImage(parent,name,color); }
    private Image MakeImage(Transform parent,string name,Color color){ GameObject go=new GameObject(name); go.transform.SetParent(parent,false); Image i=go.AddComponent<Image>(); i.color=color; return i; }
    private Button Button(Transform parent,string label,int size){ return MakeButton(parent,label,size,colBtn); }
    private Button MakeButton(Transform parent,string label,int size,Color bgColor){
        GameObject go=new GameObject("Button"); go.transform.SetParent(parent,false);
        Image img=go.AddComponent<Image>();
        // 强制不透明，避免下方/溢出文字透出造成“挤在一起”
        Color c = bgColor; c.a = 1f; img.color = c;
        Button b=go.AddComponent<Button>(); b.targetGraphic=img;
        ColorBlock cb=b.colors; cb.normalColor=Color.white; cb.highlightedColor=new Color(1.12f,1.12f,1.12f,1f); cb.pressedColor=new Color(0.88f,0.88f,0.88f,1f); b.colors=cb;
        Text t=MakeText(go.transform,"Text",label,size,TextAnchor.MiddleCenter,colTextPrimary);
        t.raycastTarget=false;
        RectTransform tr=t.rectTransform; tr.anchorMin=Vector2.zero; tr.anchorMax=Vector2.one; tr.offsetMin=new Vector2(6,4); tr.offsetMax=new Vector2(-6,-4);
        return b;
    }
    private GameObject CreateSection(Transform parent, string name, float preferredHeight) {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        Image bg = go.AddComponent<Image>();
        bg.color = new Color(0.16f, 0.18f, 0.22f, 1f);
        bg.raycastTarget = false;
        LayoutElement le = go.AddComponent<LayoutElement>();
        le.preferredHeight = preferredHeight;
        le.minHeight = preferredHeight;
        le.flexibleHeight = 0f;
        return go;
    }
    private GameObject CreateStackSection(Transform parent, string name, int topBottomPadding, int spacing, float minHeight) {
        GameObject go = CreateSection(parent, name, minHeight);
        VerticalLayoutGroup vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(8, 8, topBottomPadding, topBottomPadding);
        vlg.spacing = spacing;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlWidth = true;
        vlg.childControlHeight = false;
        vlg.childForceExpandWidth = true;
        vlg.childForceExpandHeight = false;
        ContentSizeFitter fit = go.AddComponent<ContentSizeFitter>();
        fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return go;
    }
    private GameObject CreateRow(Transform parent, string name, float height, int spacing, bool padded) {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        // 不透明底，挡住上方溢出文字
        Image rowBg = go.AddComponent<Image>();
        rowBg.color = new Color(0.18f, 0.20f, 0.26f, 1f);
        rowBg.raycastTarget = false;
        HorizontalLayoutGroup hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.padding = padded ? new RectOffset(2, 2, 2, 2) : new RectOffset(2, 2, 2, 2);
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.childControlWidth = true;
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = true;
        hlg.childForceExpandHeight = true;
        LayoutElement le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;
        le.flexibleHeight = 0f;
        return go;
    }
    private void SetFlexibleItem(GameObject go, float minWidth, float flexibleWidth) {
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        if (minWidth > 0f) le.minWidth = minWidth;
        le.flexibleWidth = flexibleWidth;
    }
    private void SetFixedHeight(GameObject go, float height) {
        LayoutElement le = go.GetComponent<LayoutElement>();
        if (le == null) le = go.AddComponent<LayoutElement>();
        le.preferredHeight = height;
        le.minHeight = height;
    }
    private void StretchFull(RectTransform rt, float left, float right, float top, float bottom) {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }
    private Toggle MakeToggle(Transform parent, string label, bool initial) {
        GameObject go = new GameObject("Toggle"); go.transform.SetParent(parent, false);
        RectTransform goRt = go.AddComponent<RectTransform>();
        // 在 VerticalLayoutGroup 中使用顶部拉伸宽度，而不是填满父级
        goRt.anchorMin = new Vector2(0, 1); goRt.anchorMax = new Vector2(1, 1);
        goRt.pivot = new Vector2(0.5f, 1);
        goRt.sizeDelta = new Vector2(0, 40);
        Image rowBg = go.AddComponent<Image>();
        rowBg.color = new Color(0.14f, 0.16f, 0.20f, 0.55f);
        rowBg.raycastTarget = true;

        GameObject box = new GameObject("Box"); box.transform.SetParent(go.transform, false);
        Image boxImg = box.AddComponent<Image>(); boxImg.color = new Color(0.10f, 0.12f, 0.16f, 1f);
        RectTransform boxRt = box.GetComponent<RectTransform>();
        boxRt.anchorMin = new Vector2(0, 0.5f); boxRt.anchorMax = new Vector2(0, 0.5f);
        boxRt.pivot = new Vector2(0, 0.5f);
        boxRt.anchoredPosition = new Vector2(10, 0);
        boxRt.sizeDelta = new Vector2(isVRMode ? 26f : 22f, isVRMode ? 26f : 22f);

        GameObject check = new GameObject("Check"); check.transform.SetParent(box.transform, false);
        Image checkImg = check.AddComponent<Image>(); checkImg.color = colAccent;
        RectTransform chkRt = check.GetComponent<RectTransform>();
        chkRt.anchorMin = new Vector2(0.18f, 0.18f); chkRt.anchorMax = new Vector2(0.82f, 0.82f);
        chkRt.offsetMin = Vector2.zero; chkRt.offsetMax = Vector2.zero;

        Toggle tg = go.AddComponent<Toggle>(); tg.targetGraphic = rowBg; tg.graphic = checkImg; tg.isOn = initial;
        Text lbl = MakeText(go.transform, "Label", label, isVRMode ? 15 : 14, TextAnchor.MiddleLeft, colTextPrimary);
        lbl.horizontalOverflow = HorizontalWrapMode.Wrap;
        lbl.verticalOverflow = VerticalWrapMode.Truncate;
        RectTransform lblRt = lbl.rectTransform;
        lblRt.anchorMin = Vector2.zero; lblRt.anchorMax = Vector2.one;
        lblRt.offsetMin = new Vector2(isVRMode ? 48f : 42f, 4); lblRt.offsetMax = new Vector2(-10, -4);
        return tg;
    }
    private InputField MakeInput(Transform parent,string placeholder){ GameObject go=new GameObject("Input"); go.transform.SetParent(parent,false); Image img=go.AddComponent<Image>(); img.color=new Color(0.94f,0.95f,0.97f,1f); InputField input=go.AddComponent<InputField>(); Text txt=Text(go.transform,"Text","",16,TextAnchor.MiddleLeft); txt.color=colTextPrimary; Rect(txt.rectTransform,8,0,390,38); Text ph=Text(go.transform,"Placeholder",placeholder,16,TextAnchor.MiddleLeft); ph.color=colTextDim; Rect(ph.rectTransform,8,0,390,38); input.textComponent=txt; input.placeholder=ph; return input; }
    private string ShortUid(string uid){ if(uid==null) return ""; if(uid.Length<=22) return uid; int dot=uid.IndexOf('.'); if(dot>0 && dot<uid.Length-1) return uid.Substring(dot+1); return uid.Substring(0,22)+"..."; }
    private void Rect(RectTransform rt,float x,float y,float w,float h){ rt.anchorMin=new Vector2(0,1); rt.anchorMax=new Vector2(0,1); rt.pivot=new Vector2(0,1); rt.anchoredPosition=new Vector2(x,-y); rt.sizeDelta=new Vector2(w,h); }
}


