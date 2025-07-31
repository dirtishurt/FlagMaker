using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using System.Reflection;
using System.Diagnostics;
using BepInEx.Configuration;
using Debug = UnityEngine.Debug;

namespace FlagMaker
{
    // A custom struct to hold color and UV data.
    public struct ColorUvPair
    {
        public Color32 color;
        public Vector2 uv;
    }

    /// <summary>
    /// This helper component handles asynchronous file loading.
    /// </summary>
    public class TextureLoader : MonoBehaviour
    {
        public static TextureLoader Instance;

        void Awake()
        {
            Instance = this;
        }

        public void LoadTextureFromPath(string filePath, Action<Texture2D> callback)
        {
            if (!Path.IsPathRooted(filePath))
            {
                filePath = Path.Combine(Directory.GetCurrentDirectory(), filePath);
            }

            StartCoroutine(LoadTextureCoroutine(filePath, callback));
        }

        private IEnumerator LoadTextureCoroutine(string filePath, Action<Texture2D> callback)
        {
            WWW www = new WWW("file://" + filePath);
            yield return www;

            if (!string.IsNullOrEmpty(www.error))
            {
                UnityEngine.Debug.LogError("[FlagMaker] WWW Error loading texture: " + www.error);
                callback(null);
                yield break;
            }

            Texture2D texture = new Texture2D(2, 2);
            www.LoadImageIntoTexture(texture);
            callback(texture);
        }
    }
    
    [BepInPlugin("com.Dirtishurt.FlagMaker", "FlagMaker", "1.2.3")]
    public class ImageProcessorPlugin : BaseUnityPlugin
    {
        // --- Constants ---
        private const string SavedFlagsFolderName = "FlagMaker_SavedFlags";
        private const int TargetWidth = 100;
        private const int TargetHeight = 66;

        // --- Configuration ---
        private ConfigEntry<string> sourceImagePath;
        private ConfigEntry<KeyCode> toggleKey;
        
        // --- File Paths ---
        private string PaletteFileName;
        private string savedFlagsPath;


        // --- UI Input Fields ---
        private float brightness = 0f;
        private float sharpen = 0f;
        private float contrast = 1.8f;
        private int noise = 3;

        // --- Runtime UI Variables ---
        private bool showWindow = false;
        private Rect windowRect = new Rect(20, 20, 450, 600);
        private string statusMessage = "Ready.";
        private bool showFileBrowser = false;
        private Rect fileBrowserRect = new Rect(40, 40, 600, 500);
        private string currentBrowserPath;
        private Action<string> onFileSelectedCallback;
        private Vector2 fileBrowserScrollPosition;
        private Vector2 savedFlagsScrollPosition;
        private string searchQuery = "";

        // --- GUI Style Variables ---
        private bool stylesInitialized = false;
        private GUIStyle windowStyle, labelStyle, buttonStyle, headerLabelStyle, textFieldStyle;

        // --- Data Structures ---
        private List<ColorUvPair> colorMap;
        private List<ColorUvPair> grayMap;
        private List<string> savedFlagFiles = new List<string>();

        void Awake()
        {
            GameObject loaderObject = new GameObject("FlagMaker_TextureLoader");
            loaderObject.AddComponent<TextureLoader>();
            DontDestroyOnLoad(loaderObject);

            // Dynamically set paths based on the plugin's actual location
            string pluginDirectory = Path.GetDirectoryName(Info.Location);
            Debug.Log(pluginDirectory);
            PaletteFileName = Path.Combine(pluginDirectory, "palette.png");
            savedFlagsPath = Path.Combine(pluginDirectory, SavedFlagsFolderName);

            sourceImagePath = Config.Bind("1. File Paths", "SourceImagePath", "source.png",
                "Path to the source PNG image.");
            toggleKey = Config.Bind("2. Hotkey", "ToggleWindowKey", KeyCode.F10,
                "Key to press to show/hide the processor window.");

            currentBrowserPath = Directory.GetCurrentDirectory();

            if (!Directory.Exists(savedFlagsPath))
            {
                Directory.CreateDirectory(savedFlagsPath);
            }
            RefreshSavedFlags();
            
            Logger.LogInfo("FlagMaker Plugin loaded!");
        }

        void Update()
        {
            if (Input.GetKeyDown(toggleKey.Value))
            {
                showWindow = !showWindow;
                
                if (showWindow)
                {
                    stylesInitialized = false;
                }
                
                if (!showWindow)
                {
                    showFileBrowser = false;
                }
            }
        }

        void OnGUI()
        {
            if (!stylesInitialized)
            {
                InitializeStyles();
            }

            if (showWindow)
                windowRect = GUILayout.Window(12345, windowRect, DrawWindow, "Image Processor", windowStyle);
            if (showFileBrowser)
                fileBrowserRect = GUILayout.Window(67890, fileBrowserRect, DrawFileBrowser, "Select a PNG File",
                    windowStyle);
        }

        void DrawWindow(int windowID)
        {
            GUILayout.Label("Press F10 to hide this window.", labelStyle);
            GUILayout.Label("Status: " + statusMessage, labelStyle);
            GUILayout.Space(10);

            GUILayout.Label("1. Create New Flag", headerLabelStyle);

            GUILayout.BeginHorizontal();
            GUILayout.Label("Source PNG: " + Path.GetFileName(sourceImagePath.Value), labelStyle, GUILayout.Width(300));
            if (GUILayout.Button("Select...", buttonStyle))
            {
                OpenFileBrowser(path =>
                {
                    sourceImagePath.Value = path;
                    Config.Save();
                });
            }
            GUILayout.EndHorizontal();
            
            GUILayout.Space(15);
            GUILayout.Label("2. Adjustments", headerLabelStyle);

            GUILayout.Label("Brightness: " + brightness.ToString("F2"), labelStyle);
            brightness = GUILayout.HorizontalSlider(brightness, -1.0f, 1.0f);
            
            GUILayout.Label("Sharpen: " + sharpen.ToString("F2"), labelStyle);
            sharpen = GUILayout.HorizontalSlider(sharpen, 0.0f, 2.0f);

            GUILayout.Label("Contrast: " + contrast.ToString("F2"), labelStyle);
            contrast = GUILayout.HorizontalSlider(contrast, 1.0f, 10.0f);

            GUILayout.Label("Noise Reduction: " + noise, labelStyle);
            noise = (int)GUILayout.HorizontalSlider(noise, 1, 9);
            if (noise % 2 == 0) noise++;

            if (GUILayout.Button("Generate and Set Flag", buttonStyle))
            {
                statusMessage = "Loading images...";
                TextureLoader.Instance.LoadTextureFromPath(sourceImagePath.Value, OnSourceTextureLoaded);
            }

            GUILayout.Space(15);
            GUILayout.Label("3. Saved Flags", headerLabelStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh List", buttonStyle))
            {
                RefreshSavedFlags();
            }
            if (GUILayout.Button("Open Folder", buttonStyle))
            {
                try
                {
                    Process.Start(savedFlagsPath);
                }
                catch (Exception e)
                {
                    statusMessage = "Error opening folder.";
                    Logger.LogError("Could not open saved flags folder: " + e.Message);
                }
            }
            GUILayout.EndHorizontal();

            savedFlagsScrollPosition = GUILayout.BeginScrollView(savedFlagsScrollPosition, GUILayout.Height(150));
            if (savedFlagFiles.Count == 0)
            {
                GUILayout.Label("No saved flags found.", labelStyle);
            }
            else
            {
                foreach (string flagPath in savedFlagFiles)
                {
                    if (GUILayout.Button(Path.GetFileNameWithoutExtension(flagPath), buttonStyle))
                    {
                        SetSavedFlag(flagPath);
                    }
                }
            }
            GUILayout.EndScrollView();

            GUI.DragWindow();
        }

        void DrawFileBrowser(int windowID)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label("Search:", labelStyle, GUILayout.Width(50));
            searchQuery = GUILayout.TextField(searchQuery, textFieldStyle);
            if (GUILayout.Button("Close", buttonStyle, GUILayout.Width(60)))
            {
                showFileBrowser = false;
                searchQuery = "";
                return;
            }
            GUILayout.EndHorizontal();

            GUILayout.Label("Current Path: " + currentBrowserPath, labelStyle);

            fileBrowserScrollPosition = GUILayout.BeginScrollView(fileBrowserScrollPosition);
            try
            {
                DirectoryInfo parentDir = Directory.GetParent(currentBrowserPath);
                if (parentDir != null)
                {
                    if (GUILayout.Button(".. [Up]", buttonStyle))
                    {
                        currentBrowserPath = parentDir.FullName;
                        searchQuery = "";
                    }
                }

                string[] directories = Directory.GetDirectories(currentBrowserPath);
                foreach (string dir in directories)
                {
                    string dirName = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(searchQuery) || dirName.ToLower().Contains(searchQuery.ToLower()))
                    {
                        if (GUILayout.Button("[D] " + dirName, buttonStyle))
                        {
                            currentBrowserPath = dir;
                            searchQuery = "";
                        }
                    }
                }

                string[] extensions = { ".png", ".jpg",  ".jpeg", ".bmp" }; 
                string[] files = Directory.GetFiles(currentBrowserPath)
                    .Where(f => extensions.Contains(Path.GetExtension(f).ToLower())).ToArray();
                foreach (string file in files)
                {
                    string fileName = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(searchQuery) || fileName.ToLower().Contains(searchQuery.ToLower()))
                    {
                        if (GUILayout.Button("[F] " + fileName, buttonStyle))
                        {
                            onFileSelectedCallback(file);
                            showFileBrowser = false;
                            searchQuery = "";
                        }
                    }
                }
            }
            catch (Exception e)
            {
                GUILayout.Label("Error reading directory: " + e.Message, labelStyle);
            }
            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private Texture2D MakeTex(Color col)
        {
            Color[] pix = new Color[1];
            pix[0] = col;
            Texture2D result = new Texture2D(1, 1);
            result.SetPixels(pix);
            result.Apply();
            DontDestroyOnLoad(result);
            return result;
        }

        private void InitializeStyles()
        {
            windowStyle = new GUIStyle(GUI.skin.window);
            windowStyle.normal.background = MakeTex(new Color(0.1f, 0.12f, 0.15f, 0.95f));
            windowStyle.onNormal.background = windowStyle.normal.background;
            windowStyle.normal.textColor = new Color(0.9f, 0.9f, 0.9f);
            windowStyle.onNormal.textColor = windowStyle.normal.textColor;

            headerLabelStyle = new GUIStyle(GUI.skin.label);
            headerLabelStyle.normal.textColor = Color.white;
            headerLabelStyle.fontStyle = FontStyle.Bold;

            labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.normal.textColor = new Color(0.8f, 0.8f, 0.8f);

            buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.normal.background = MakeTex(new Color(0.3f, 0.35f, 0.4f, 1f));
            buttonStyle.hover.background = MakeTex(new Color(0.4f, 0.45f, 0.5f, 1f));
            buttonStyle.active.background = MakeTex(new Color(0.2f, 0.25f, 0.3f, 1f));
            buttonStyle.normal.textColor = Color.white;
            buttonStyle.hover.textColor = Color.white;
            buttonStyle.active.textColor = Color.white;

            textFieldStyle = new GUIStyle(GUI.skin.textField);
            textFieldStyle.normal.background = MakeTex(new Color(0.2f, 0.22f, 0.25f, 1f));
            textFieldStyle.normal.textColor = Color.white;

            stylesInitialized = true;
        }

        private void OpenFileBrowser(Action<string> callback)
        {
            showFileBrowser = true;
            onFileSelectedCallback = callback;
        }

        private void OnSourceTextureLoaded(Texture2D sourceTexture)
        {
            if (sourceTexture == null)
            {
                statusMessage = "Error: Failed to load Source Image.";
                Logger.LogError(statusMessage);
                return;
            }
            
            TextureLoader.Instance.LoadTextureFromPath(PaletteFileName,
                (paletteTexture) => { OnBothTexturesLoaded(sourceTexture, paletteTexture); });
        }

        private void OnBothTexturesLoaded(Texture2D sourceTexture, Texture2D paletteTexture)
        {
            if (paletteTexture == null)
            {
                statusMessage = "Error: Failed to load Palette Image ('" + PaletteFileName + "').";
                Logger.LogError(statusMessage);
                return;
            }

            statusMessage = "Resizing source image...";

            Texture2D resizedSource = ResizeTexture(sourceTexture, TargetWidth, TargetHeight);

            statusMessage = "Processing...";

            CreateSplitReferenceMaps(paletteTexture);

            Texture2D processedTexture = new Texture2D(resizedSource.width, resizedSource.height);
            processedTexture.SetPixels(resizedSource.GetPixels());
            processedTexture.Apply();

            if (brightness != 0f) ApplyBrightness(processedTexture, brightness);
            if (contrast != 1.0f) ApplyContrast(processedTexture, contrast);
            if (sharpen > 0f) ApplySharpen(processedTexture, sharpen);
            if (noise > 1) ApplyMedianFilter(processedTexture, noise);

            List<string> uvList = new List<string>();
            float saturationThreshold = 0.1f;
            for (int i = 0; i < processedTexture.width; i++)
            {
                for (int j = 0; j < processedTexture.height; j++)
                {
                    Color pixelFloat = processedTexture.GetPixel(i, j);
                    Color.RGBToHSV(pixelFloat, out _, out float s, out _);
                    Color32 pixel32 = pixelFloat;
                    Vector2 uv = (s < saturationThreshold)
                        ? FindClosestUV(pixel32, grayMap)
                        : FindClosestUV(pixel32, colorMap);

                    uvList.Add(string.Format("{0:F6}:{1:F6}", uv.x, uv.y));
                }
            }

            string resultString = string.Join(",", uvList.ToArray());

            try
            {
                string newFileName = Path.GetFileNameWithoutExtension(sourceImagePath.Value) + ".txt";
                string newFilePath = Path.Combine(savedFlagsPath, newFileName);
                File.WriteAllText(newFilePath, resultString);

                SetFlagGridPlayerPref(resultString);
                RefreshSavedFlags();
            }
            catch (Exception e)
            {
                statusMessage = "Error saving flag file.";
                Logger.LogError("Could not save flag file: " + e.Message);
            }
        }
        
        private void SetFlagGridPlayerPref(string flagContent)
        {
            try
            {
                if (!string.IsNullOrEmpty(flagContent))
                {
                    PlayerPrefs.SetString("flagGrid", flagContent);
                    PlayerPrefs.Save();
                    statusMessage = "Success! Flag has been set.";
                    
                    CallLoadFlagMethod();
                }
                else
                {
                    statusMessage = "Error: Flag content was empty.";
                    Logger.LogError(statusMessage);
                }
            }
            catch (Exception ex)
            {
                statusMessage = "Error: Failed to set flag from content.";
                Logger.LogError("SetFlag Error: " + ex.Message);
            }
        }
        
        private void CallLoadFlagMethod()
        {
            try
            {
                Type drawPixelsType = AccessTools.TypeByName("DrawPixels");
                if (drawPixelsType == null)
                {
                    Logger.LogError("Could not find the 'DrawPixels' class type.");
                    return;
                }

                UnityEngine.Object drawPixelsInstance = FindObjectOfType(drawPixelsType);
                if (drawPixelsInstance == null)
                {
                    Logger.LogError("Could not find an active instance of the 'DrawPixels' class in the scene.");
                    return;
                }

                MethodInfo loadflagMethod = drawPixelsType.GetMethod("loadflag", BindingFlags.Public | BindingFlags.Instance);
                if (loadflagMethod == null)
                {
                    Logger.LogError("Could not find the 'loadflag' method in the 'DrawPixels' class.");
                    return;
                }
                
                loadflagMethod.Invoke(drawPixelsInstance, null);
                Logger.LogInfo("Successfully called DrawPixels.loadflag()");
            }
            catch (Exception e)
            {
                statusMessage = "Error calling game method.";
                Logger.LogError("An exception occurred while trying to call 'loadflag': " + e);
            }
        }

        private void RefreshSavedFlags()
        {
            savedFlagFiles.Clear();
            if (Directory.Exists(savedFlagsPath))
            {
                savedFlagFiles.AddRange(Directory.GetFiles(savedFlagsPath, "*.txt"));
            }
        }

        private void SetSavedFlag(string flagPath)
        {
            try
            {
                if (File.Exists(flagPath))
                {
                    string flagContent = File.ReadAllText(flagPath);
                    SetFlagGridPlayerPref(flagContent);
                    statusMessage = "Loaded and set flag: " + Path.GetFileNameWithoutExtension(flagPath);
                }
            }
            catch (Exception e)
            {
                statusMessage = "Error loading saved flag.";
                Logger.LogError("Could not load saved flag: " + e.Message);
            }
        }

        private void CreateSplitReferenceMaps(Texture2D paletteImage)
        {
            Texture2D opaquePalette = new Texture2D(paletteImage.width, paletteImage.height);
            Color[] pixels = paletteImage.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].a = 1.0f;
            }

            opaquePalette.SetPixels(pixels);
            opaquePalette.Apply();

            colorMap = new List<ColorUvPair>();
            grayMap = new List<ColorUvPair>();
            HashSet<Color32> seenColors = new HashSet<Color32>();
            float saturationThreshold = 0.1f;

            int width = opaquePalette.width;
            int height = opaquePalette.height;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color pixelFloat = opaquePalette.GetPixel(x, y);
                    Color32 pixel32 = pixelFloat;

                    if (seenColors.Contains(pixel32)) continue;
                    seenColors.Add(pixel32);

                    float u = (float)x / (width - 1);
                    float v = (float)y / (height - 1);

                    Color.RGBToHSV(pixelFloat, out _, out float s, out _);
                    ColorUvPair pair = new ColorUvPair { color = pixel32, uv = new Vector2(u, v) };
                    if (s < saturationThreshold)
                    {
                        grayMap.Add(pair);
                    }
                    else
                    {
                        colorMap.Add(pair);
                    }
                }
            }
        }

        private Vector2 FindClosestUV(Color32 pixelColor, List<ColorUvPair> referenceMap)
        {
            int minSqrDist = int.MaxValue;
            Vector2 bestUv = Vector2.zero;
            foreach (ColorUvPair entry in referenceMap)
            {
                int r_dist = pixelColor.r - entry.color.r;
                int g_dist = pixelColor.g - entry.color.g;
                int b_dist = pixelColor.b - entry.color.b;
                int sqrDist = r_dist * r_dist + g_dist * g_dist + b_dist * b_dist;
                if (sqrDist < minSqrDist)
                {
                    minSqrDist = sqrDist;
                    bestUv = entry.uv;
                    if (minSqrDist == 0) break;
                }
            }
            return bestUv;
        }
        
        private void ApplyBrightness(Texture2D texture, float amount)
        {
            Color[] pixels = texture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].r += amount;
                pixels[i].g += amount;
                pixels[i].b += amount;
            }
            texture.SetPixels(pixels);
            texture.Apply();
        }

        private void ApplyContrast(Texture2D texture, float factor)
        {
            Color[] pixels = texture.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i].r = 0.5f + factor * (pixels[i].r - 0.5f);
                pixels[i].g = 0.5f + factor * (pixels[i].g - 0.5f);
                pixels[i].b = 0.5f + factor * (pixels[i].b - 0.5f);
            }

            texture.SetPixels(pixels);
            texture.Apply();
        }
        
        private void ApplySharpen(Texture2D texture, float amount)
        {
            Color[] originalPixels = texture.GetPixels();
            Color[] sharpenedPixels = new Color[originalPixels.Length];
            int width = texture.width;
            int height = texture.height;

            float[] kernel = { 0, -1, 0, -1, 5, -1, 0, -1, 0 };
            
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Handle edges by just copying the original pixel
                    if (y == 0 || y >= height - 1 || x == 0 || x >= width - 1) {
                        sharpenedPixels[y * width + x] = originalPixels[y * width + x];
                        continue;
                    }

                    float r = 0, g = 0, b = 0;
                    int kernelIndex = 0;

                    for (int ky = -1; ky <= 1; ky++)
                    {
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            Color c = originalPixels[(y + ky) * width + (x + kx)];
                            r += c.r * kernel[kernelIndex];
                            g += c.g * kernel[kernelIndex];
                            b += c.b * kernel[kernelIndex];
                            kernelIndex++;
                        }
                    }

                    Color originalColor = originalPixels[y * width + x];
                    sharpenedPixels[y * width + x] = new Color(
                        Mathf.Lerp(originalColor.r, r, amount),
                        Mathf.Lerp(originalColor.g, g, amount),
                        Mathf.Lerp(originalColor.b, b, amount),
                        originalColor.a);
                }
            }
            texture.SetPixels(sharpenedPixels);
            texture.Apply();
        }

        private void ApplyMedianFilter(Texture2D texture, int size)
        {
            Color[] originalPixels = texture.GetPixels();
            Color[] filteredPixels = new Color[originalPixels.Length];
            int width = texture.width;
            int height = texture.height;
            int halfSize = size / 2;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    List<float> r_values = new List<float>();
                    List<float> g_values = new List<float>();
                    List<float> b_values = new List<float>();
                    for (int ky = -halfSize; ky <= halfSize; ky++)
                    {
                        for (int kx = -halfSize; kx <= halfSize; kx++)
                        {
                            int nx = Mathf.Clamp(x + kx, 0, width - 1);
                            int ny = Mathf.Clamp(y + ky, 0, height - 1);
                            Color pixel = originalPixels[ny * width + nx];
                            r_values.Add(pixel.r);
                            g_values.Add(pixel.g);
                            b_values.Add(pixel.b);
                        }
                    }

                    r_values.Sort();
                    g_values.Sort();
                    b_values.Sort();
                    int medianIndex = r_values.Count / 2;
                    filteredPixels[y * width + x] = new Color(r_values[medianIndex], g_values[medianIndex],
                        b_values[medianIndex], originalPixels[y * width + x].a);
                }
            }

            texture.SetPixels(filteredPixels);
            texture.Apply();
        }

        private Texture2D ResizeTexture(Texture2D source, int newWidth, int newHeight)
        {
            RenderTexture rt = RenderTexture.GetTemporary(newWidth, newHeight);
            rt.filterMode = FilterMode.Bilinear;
            RenderTexture.active = rt;
            Graphics.Blit(source, rt);

            Texture2D newTexture = new Texture2D(newWidth, newHeight);
            newTexture.ReadPixels(new Rect(0, 0, newWidth, newHeight), 0, 0);
            newTexture.Apply();

            RenderTexture.active = null;
            RenderTexture.ReleaseTemporary(rt);
            return newTexture;
        }
    }
}