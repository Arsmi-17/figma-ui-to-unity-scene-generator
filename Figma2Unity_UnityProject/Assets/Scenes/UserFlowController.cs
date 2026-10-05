using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class UserFlowController : MonoBehaviour
{
    [Header("Screens")]
    [SerializeField] private GameObject registerScreen;
    [SerializeField] private GameObject captureScreen;
    [SerializeField] private GameObject messageScreen;
    [SerializeField] private GameObject reviewScreen;

    [Header("Register")]
    [SerializeField] private TMP_InputField nameInputField;
    [SerializeField] private Button continueButton;

    [Header("Capture")]
    [SerializeField] private RawImage cameraRawImage;
    [SerializeField] private Button captureButton;

    [Header("Message")]
    [SerializeField] private TMP_InputField messageInputField;
    [SerializeField] private Button submitButton;

    [Header("Review")]
    [SerializeField] private TMP_Text reviewNameText;
    [SerializeField] private TMP_Text reviewMessageText;
    [SerializeField] private Button homeButton;

    [Header("Camera")]
    [SerializeField] private bool useWebcam;
    [SerializeField] private bool allowWebcamInEditor;
    [SerializeField] private bool startWebcamAutomatically;
    [SerializeField] private bool continueWhenCameraUnavailable = true;
    [SerializeField] private Vector2Int requestedCameraResolution = new Vector2Int(1280, 720);
    [SerializeField] private int requestedCameraFps = 30;
    [SerializeField] private string preferredCameraName;

    private WebCamTexture webcamTexture;
    private Texture2D capturedPhoto;

    private string userName;
    private string userMessage;
    private bool photoCaptured;
    private bool listenersRegistered;
    private Coroutine webcamStartRoutine;

    private void OnEnable()
    {
        RegisterButtonListeners();
    }

    private void Start()
    {
        ShowRegisterScreen();
    }

    private void OnDisable()
    {
        UnregisterButtonListeners();
        StopWebcam();
    }

    // --------------------------------------------------
    // REGISTER
    // --------------------------------------------------

    private void OnContinueClicked()
    {
        userName = nameInputField != null ? nameInputField.text.Trim() : string.Empty;

        if (string.IsNullOrEmpty(userName))
        {
            Debug.Log("Please enter your name.");
            return;
        }

        ShowCaptureScreen();
    }

    // --------------------------------------------------
    // CAPTURE
    // --------------------------------------------------

    private void ShowCaptureScreen()
    {
        SetScreenActive(registerScreen, false);
        SetScreenActive(captureScreen, true);
        SetScreenActive(messageScreen, false);
        SetScreenActive(reviewScreen, false);

        photoCaptured = false;
        if (captureButton != null)
            captureButton.interactable = true;
        SetCaptureButtonText("Capture");

        if (useWebcam && startWebcamAutomatically)
            QueueWebcamStart();
        else if (cameraRawImage != null)
            cameraRawImage.texture = null;
    }

    private void QueueWebcamStart()
    {
        if (!CanStartWebcam())
        {
            if (continueWhenCameraUnavailable)
                SetCaptureButtonText("Next");
            return;
        }

        if (webcamStartRoutine != null)
            StopCoroutine(webcamStartRoutine);

        webcamStartRoutine = StartCoroutine(StartWebcamDelayed());
    }

    private IEnumerator StartWebcamDelayed()
    {
        yield return null;
        yield return null;

        StartWebcam();
        webcamStartRoutine = null;
    }

    private bool StartWebcam()
    {
        if (cameraRawImage == null)
        {
            Debug.LogWarning("Camera RawImage is not assigned.");
            return false;
        }

        try
        {
            if (webcamTexture != null)
            {
                if (!webcamTexture.isPlaying)
                    webcamTexture.Play();

                cameraRawImage.texture = webcamTexture;
                return webcamTexture.isPlaying;
            }

            WebCamDevice[] devices = WebCamTexture.devices;
            if (devices == null || devices.Length == 0)
            {
                Debug.LogWarning("No webcam found.");
                return false;
            }

            string deviceName = GetCameraDeviceName(devices);
            webcamTexture = new WebCamTexture(
                deviceName,
                Mathf.Max(1, requestedCameraResolution.x),
                Mathf.Max(1, requestedCameraResolution.y),
                Mathf.Max(1, requestedCameraFps)
            );

            cameraRawImage.texture = webcamTexture;
            webcamTexture.Play();
            return webcamTexture.isPlaying;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not start webcam: {ex.Message}");
            if (cameraRawImage != null)
                cameraRawImage.texture = null;
            return false;
        }
    }

    private void OnCaptureClicked()
    {
        if (photoCaptured)
        {
            ShowMessageScreen();
            return;
        }

        if (!useWebcam)
        {
            Debug.Log("Webcam is disabled. Continuing flow without a captured photo.");
            ShowMessageScreen();
            return;
        }

        if (!CanStartWebcam())
        {
            if (continueWhenCameraUnavailable)
                ShowMessageScreen();
            return;
        }

        if (webcamTexture == null || !webcamTexture.isPlaying)
        {
            if (StartWebcam())
            {
                SetCaptureButtonText("Capture");
                return;
            }

            Debug.LogWarning("Camera is not available.");
            if (continueWhenCameraUnavailable)
                ShowMessageScreen();
            return;
        }

        capturedPhoto = new Texture2D(
            webcamTexture.width,
            webcamTexture.height,
            TextureFormat.RGB24,
            false
        );

        capturedPhoto.SetPixels(webcamTexture.GetPixels());
        capturedPhoto.Apply();

        cameraRawImage.texture = capturedPhoto;

        photoCaptured = true;

        SetCaptureButtonText("Next");

        Debug.Log("Photo captured.");
    }

    // --------------------------------------------------
    // MESSAGE
    // --------------------------------------------------

    private void ShowMessageScreen()
    {
        StopWebcam();

        SetScreenActive(captureScreen, false);
        SetScreenActive(messageScreen, true);
        SetScreenActive(reviewScreen, false);
    }

    private void OnSubmitClicked()
    {
        userMessage = messageInputField != null ? messageInputField.text.Trim() : string.Empty;

        ShowReviewScreen();
    }

    // --------------------------------------------------
    // REVIEW
    // --------------------------------------------------

    private void ShowReviewScreen()
    {
        SetScreenActive(messageScreen, false);
        SetScreenActive(reviewScreen, true);

        if (reviewNameText != null)
            reviewNameText.text = userName;
        if (reviewMessageText != null)
            reviewMessageText.text = userMessage;
    }

    // --------------------------------------------------
    // HOME
    // --------------------------------------------------

    private void OnHomeClicked()
    {
        ResetData();
        ShowRegisterScreen();
    }

    private void ShowRegisterScreen()
    {
        SetScreenActive(registerScreen, true);
        SetScreenActive(captureScreen, false);
        SetScreenActive(messageScreen, false);
        SetScreenActive(reviewScreen, false);

        if (nameInputField != null)
            nameInputField.text = "";
        if (messageInputField != null)
            messageInputField.text = "";

        if (capturedPhoto != null)
        {
            Destroy(capturedPhoto);
            capturedPhoto = null;
        }

        photoCaptured = false;

        SetCaptureButtonText("Capture");
    }

    // --------------------------------------------------
    // CAMERA
    // --------------------------------------------------

    private void StopWebcam()
    {
        if (webcamStartRoutine != null)
        {
            StopCoroutine(webcamStartRoutine);
            webcamStartRoutine = null;
        }

        try
        {
            if (webcamTexture != null && webcamTexture.isPlaying)
                webcamTexture.Stop();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Could not stop webcam: {ex.Message}");
        }
    }

    // --------------------------------------------------
    // RESET
    // --------------------------------------------------

    private void ResetData()
    {
        userName = "";
        userMessage = "";
        photoCaptured = false;

        StopWebcam();

        if (capturedPhoto != null)
        {
            Destroy(capturedPhoto);
            capturedPhoto = null;
        }

        if (cameraRawImage != null)
            cameraRawImage.texture = null;
    }

    private void OnDestroy()
    {
        UnregisterButtonListeners();
        StopWebcam();

        if (webcamTexture != null)
        {
            Destroy(webcamTexture);
            webcamTexture = null;
        }
    }

    private void RegisterButtonListeners()
    {
        if (listenersRegistered)
            return;

        if (continueButton != null)
            continueButton.onClick.AddListener(OnContinueClicked);
        if (captureButton != null)
            captureButton.onClick.AddListener(OnCaptureClicked);
        if (submitButton != null)
            submitButton.onClick.AddListener(OnSubmitClicked);
        if (homeButton != null)
            homeButton.onClick.AddListener(OnHomeClicked);

        listenersRegistered = true;
    }

    private void UnregisterButtonListeners()
    {
        if (!listenersRegistered)
            return;

        if (continueButton != null)
            continueButton.onClick.RemoveListener(OnContinueClicked);
        if (captureButton != null)
            captureButton.onClick.RemoveListener(OnCaptureClicked);
        if (submitButton != null)
            submitButton.onClick.RemoveListener(OnSubmitClicked);
        if (homeButton != null)
            homeButton.onClick.RemoveListener(OnHomeClicked);

        listenersRegistered = false;
    }

    private static void SetScreenActive(GameObject screen, bool active)
    {
        if (screen != null)
            screen.SetActive(active);
    }

    private void SetCaptureButtonText(string text)
    {
        if (captureButton == null)
            return;

        TMP_Text label = captureButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = text;
    }

    private bool CanStartWebcam()
    {
#if UNITY_EDITOR
        if (!allowWebcamInEditor)
        {
            Debug.LogWarning("Webcam is enabled, but editor webcam startup is blocked to avoid Unity editor crashes. Enable Allow Webcam In Editor only if this machine's camera driver is stable.");
            return false;
        }
#endif
        return true;
    }

    private string GetCameraDeviceName(WebCamDevice[] devices)
    {
        if (!string.IsNullOrWhiteSpace(preferredCameraName))
        {
            for (int i = 0; i < devices.Length; i++)
            {
                if (string.Equals(devices[i].name, preferredCameraName, StringComparison.OrdinalIgnoreCase))
                    return devices[i].name;
            }

            Debug.LogWarning($"Preferred camera '{preferredCameraName}' was not found. Using '{devices[0].name}' instead.");
        }

        return devices[0].name;
    }
}
