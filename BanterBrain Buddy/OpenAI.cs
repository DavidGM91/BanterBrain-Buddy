using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OpenAI;
using OpenAI.Audio;
using OpenAI.Chat;
using OpenAI.Models;
using System;
using System.ClientModel;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace BanterBrain_Buddy
{
    /// <summary>
    /// CODING RULES:
    /// •	Local variables, private instance, static fields and method parameters should be camelCase.
    /// •	Methods, constants, properties, events and classes should be PascalCase.
    /// </summary>
    /// 

    internal class OpenAI
    {

        private static readonly log4net.ILog _bBBlog = log4net.LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        // how many chat history messages we keep and send along for context
        private const int maxChatHistoryMessages = 40;
        // model that is used when the user setting is empty or a legacy/retired value
        private const string defaultChatModel = "gpt-5-mini";

        private int SelectedOutputDevice;
        private OpenAIClient _OpenAPI;
        private readonly List<ChatMessage> _chatHistory = new();
        public string OpenAIAPIKey { get; set; }

        private SettingsManager UserSettingsManager = SettingsManager.Instance;

        private void SetSelectedOutputDevice(string OutputDevice)
        {
            _bBBlog.Info($"Setting selected output device for Native TTS to: {OutputDevice}");
            for (int i = 0; i < NAudio.Wave.WaveOut.DeviceCount; i++)
            {
                var tmpOut = NAudio.Wave.WaveOut.GetCapabilities(i).ProductName;
                if (OutputDevice.StartsWith(tmpOut))
                {
                    _bBBlog.Debug($"Selected outputdevice = {tmpOut}");
                    SelectedOutputDevice = i;
                }
            }
        }

        // Validates an API key by listing the models it gives access to.
        // This is a free (non token consuming) authenticated call, used by the UI to test keys.
        public static async Task<bool> ValidateApiKeyAsync(string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                return false;
            }
            try
            {
                OpenAIModelClient modelClient = new(apiKey);
                await modelClient.GetModelsAsync();
                return true;
            }
            catch (ClientResultException)
            {
                return false;
            }
        }

        // Function to validate the API key with a timeout
        // if it times out, we will retry once
        public async Task<bool> OpenAICheckAPIKey()
        {
            _bBBlog.Debug("Verifying OpenAI API key");
            if (string.IsNullOrEmpty(UserSettingsManager.Settings.GPTAPIKey))
            {
                _bBBlog.Error("OpenAI API Key is missing or bad");
                return false;
            }
            else
            {
                OpenAIAPIKey = UserSettingsManager.Settings.GPTAPIKey;
                _OpenAPI ??= new(OpenAIAPIKey);

                // Function to validate the API key with a timeout
                async Task<bool> ValidateAPIKeyWithTimeout()
                {
                    var validateTask = _OpenAPI.GetOpenAIModelClient().GetModelsAsync();
                    var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15)); // 15 seconds timeout

                    var completedTask = await Task.WhenAny(validateTask, timeoutTask);
                    if (completedTask == timeoutTask)
                    {
                        _bBBlog.Error("OpenAI API Key validation timed out");
                        return false;
                    }

                    try
                    {
                        await validateTask;
                        return true;
                    }
                    catch (ClientResultException ex)
                    {
                        _bBBlog.Error("OpenAI API Key rejected by server: " + ex.Status + " " + ex.Message);
                        return false;
                    }
                }

                // First attempt
                if (await ValidateAPIKeyWithTimeout())
                {
                    _bBBlog.Info("OpenAI API Key is valid");
                    return true;
                }

                // Retry once if the first attempt times out
                _bBBlog.Info("Retrying OpenAI API Key validation");
                if (await ValidateAPIKeyWithTimeout())
                {
                    _bBBlog.Info("OpenAI API Key is valid");
                    return true;
                }
                else
                {
                    _bBBlog.Error("OpenAI API Key is invalid");
                    OpenAIAPIKey = null;
                    return false;
                }
            }
        }

        public async Task<string> OpenAISTT(string audioFile)
        {
            _bBBlog.Info("Sending to OpenAI STT with language: " + UserSettingsManager.Settings.WhisperSpeechRecognitionComboBox);
            string ISOLanguage;
            switch (UserSettingsManager.Settings.WhisperSpeechRecognitionComboBox)
            {
                case "Dutch":
                    ISOLanguage = "nl";
                    break;
                case "Danish":
                    ISOLanguage = "da";
                    break;
                case "English":
                    ISOLanguage = "en";
                    break;
                case "French":
                    ISOLanguage = "fr";
                    break;
                case "German":
                    ISOLanguage = "de";
                    break;
                case "Italian":
                    ISOLanguage = "it";
                    break;
                case "Japanese":
                    ISOLanguage = "ja";
                    break;
                case "Norwegian":
                    ISOLanguage = "no";
                    break;
                case "Polish":
                    ISOLanguage = "pl";
                    break;
                case "Spanish":
                    ISOLanguage = "es";
                    break;
                case "Swedish":
                    ISOLanguage = "sv";
                    break;
                default:
                    _bBBlog.Error("OpenAI STT language not supported, using english");
                    ISOLanguage = "en";
                    break;
            }

            if (_OpenAPI == null)
            { 
            _OpenAPI = new(UserSettingsManager.Settings.GPTAPIKey);
            OpenAIAPIKey = UserSettingsManager.Settings.GPTAPIKey;
            }
            AudioClient whisperClient = _OpenAPI.GetAudioClient("whisper-1");
            AudioTranscriptionOptions transcriptionOptions = new()
            {
                Language = ISOLanguage
            };
            try
            {
                ClientResult<AudioTranscription> STTResult = await whisperClient.TranscribeAudioAsync(audioFile, transcriptionOptions);

                if (STTResult?.Value?.Text == null)
                {
                    _bBBlog.Error("OpenAI STT failed");
                    return null;
                }
                return STTResult.Value.Text;
            }
            catch (ClientResultException ex)
            {
                _bBBlog.Error("OpenAI STT failed: " + ex.Status + " " + ex.Message);
                return null;
            }
        }

        //convert the speed range from 0.25 to 4.0 using the -100 to 100 slider
        double ConvertRange(int inputValue)
        {
            // Define the input range and corresponding speed range
            int inputMin = -100, inputMax = 100;
            float speedAtZero = 1.0f, speedMin = 0.25f, speedMax = 4.0f;

            // Calculate the slope for the negative and positive ranges
            float slopeForNegativeRange = (speedAtZero - speedMin) / -inputMin; // From -100 to 0
            float slopeForPositiveRange = (speedMax - speedAtZero) / inputMax;  // From 0 to +100

            // Apply the piecewise linear mapping
            if (inputValue <= 0)
            {
                return speedAtZero + (inputValue * slopeForNegativeRange);
            }
            else // inputValue > 0
            {
                return speedAtZero + (inputValue * slopeForPositiveRange);
            }
        }

        public async Task<bool> OpenAITTS(string text, string outputDevice, string voice, int speed, int volumeControl)
        {
            SetSelectedOutputDevice(outputDevice);
            if (_OpenAPI == null)
            {
                _OpenAPI = new(UserSettingsManager.Settings.GPTAPIKey);
                OpenAIAPIKey = UserSettingsManager.Settings.GPTAPIKey;
            }
                //alloy, echo, fable, onyx, nova, and shimmer
            AudioClient ttsClient = _OpenAPI.GetAudioClient("tts-1");
            SpeechGenerationOptions ttsRequestArgs = new()
            {
                //speed 0.25 to 4.0
                SpeedRatio = (float)ConvertRange(speed),
                //we use WAV streams format
                ResponseFormat = GeneratedSpeechFormat.Wav,
            };
            byte[] wavBytes;
            try
            {
                ClientResult<BinaryData> TTSResult = await ttsClient.GenerateSpeechAsync(text, new GeneratedSpeechVoice(voice), ttsRequestArgs);
                if (TTSResult?.Value == null)
                {
                    _bBBlog.Error("OpenAI TTS failed");
                    return false;
                }
                wavBytes = TTSResult.Value.ToArray();
            }
            catch (ClientResultException ex)
            {
                _bBBlog.Error("OpenAI TTS failed: " + ex.Status + " " + ex.Message);
                return false;
            }
            if (wavBytes.Length == 0)
            {
                _bBBlog.Error("OpenAI TTS returned empty audio");
                return false;
            }
            // Ensure volumeControl is within the expected range
            volumeControl = Math.Clamp(volumeControl, -100, 100);

            // Convert volumeControl to a volume multiplier
            //float volumeMultiplier = (float)Math.Pow(10, volumeControl / 100.0); // Use logarithmic scale for volume control
            // Convert volumeControl to a volume multiplier
            float volumeMultiplier = 1.0f + (volumeControl / 100.0f);


            var waveOut = new WaveOut
            {
                DeviceNumber = SelectedOutputDevice
            };
            //the response is a full WAV file, so we can read it with a WaveFileReader
            var waveStream = new WaveFileReader(new MemoryStream(wavBytes));
            // Create a SampleChannel for volume control
            var sampleChannel = new SampleChannel(waveStream, false);

            // Use a VolumeSampleProvider to apply the volume multiplier
            var volumeProvider = new VolumeSampleProvider(sampleChannel)
            {
                Volume = volumeMultiplier
            };

            waveOut.Init(volumeProvider);

            waveOut.Play();
            while (waveOut.PlaybackState != PlaybackState.Stopped)
            {
                await Task.Delay(500);
            }
            waveOut.Dispose();
            return true;
        }

        // maps legacy/retired model names from older settings.json files to current models
        private string NormalizeGPTModel(string model)
        {
            switch (model)
            {
                case "gpt-3.5-turbo": // retired, use the current low cost model instead
                    _bBBlog.Info("Legacy GPT-3.5 Turbo setting detected, using " + defaultChatModel);
                    return defaultChatModel;
                case "gpt-4-omni":
                    _bBBlog.Info("Legacy gpt-4-omni setting detected, using gpt-4o");
                    return "gpt-4o";
                case "gpt-4-omni-mini":
                    _bBBlog.Info("Legacy gpt-4-omni-mini setting detected, using gpt-4o-mini");
                    return "gpt-4o-mini";
                case null or "":
                    _bBBlog.Info("No GPT Model set, using " + defaultChatModel);
                    return defaultChatModel;
                default:
                    return model;
            }
        }

        // reasoning models manage a hidden thinking budget, for these we must not send a
        // temperature or an output cap the way we do for the classic models
        private static bool IsReasoningModel(string model)
        {
            return model.StartsWith("gpt-5") || model.StartsWith("o1") || model.StartsWith("o3") || model.StartsWith("o4");
        }

        [SupportedOSPlatform("windows10.0.10240")]
        public async Task<string> GetOpenAIIGPTResponse(String UserInput, string tmpPersonaRoletext)
        {
            string gPTOutputText = "";
            _bBBlog.Info("Sending to OpenAI GPT LLM: " + UserInput);

            _OpenAPI ??= new(UserSettingsManager.Settings.GPTAPIKey);

            string model = NormalizeGPTModel(UserSettingsManager.Settings.GPTModel);
            _bBBlog.Info("GPT Model: " + model);
            ChatClient chatClient = _OpenAPI.GetChatClient(model);

            bool reasoningModel = IsReasoningModel(model);
            ChatCompletionOptions requestOptions = new();
            // reasoning models (gpt-5 and newer) reject a non default temperature and
            // manage their own output budget, so only apply these to the classic models
            if (!reasoningModel)
            {
                requestOptions.Temperature = UserSettingsManager.Settings.GPTTemperature;
                requestOptions.MaxOutputTokenCount = UserSettingsManager.Settings.GPTMaxTokens;
            }
            else if (UserSettingsManager.Settings.GPTNoReasoningCheckBox)
            {
                //the user asked for short answers: keep the model but drop it to the lowest reasoning
                //effort, otherwise the answer can be cut off before the model gets to say anything
#pragma warning disable OPENAI001 // the SDK still flags the reasoning effort level as experimental
                requestOptions.ReasoningEffortLevel = ChatReasoningEffortLevel.Minimal;
#pragma warning restore OPENAI001
                _bBBlog.Info("GPT reasoning disabled, using minimal reasoning effort");
            }
            //mood is setting the system text description
            //this is the persona role
            _bBBlog.Info("SystemRole: " + tmpPersonaRoletext);
            List<ChatMessage> messages = new()
            {
                new SystemChatMessage(tmpPersonaRoletext)
            };
            messages.AddRange(_chatHistory);
            messages.Add(new UserChatMessage(UserInput));

            try
            {
                _bBBlog.Info("ChatGPT response: ");
                AsyncCollectionResult<StreamingChatCompletionUpdate> completionUpdates = chatClient.CompleteChatStreamingAsync(messages, requestOptions);
                ChatFinishReason? finishReason = null;
                await foreach (StreamingChatCompletionUpdate completionUpdate in completionUpdates)
                {
                    foreach (ChatMessageContentPart contentPart in completionUpdate.ContentUpdate)
                    {
                        gPTOutputText += contentPart.Text;
                    }
                    //the last update tells us why the model stopped generating
                    if (completionUpdate.FinishReason != null)
                        finishReason = completionUpdate.FinishReason;
                }
                if (finishReason == ChatFinishReason.Length)
                {
                    //output was cut off by the token limit, this is the usual cause of half sentences
                    _bBBlog.Warn("GPT answer hit the output token limit (finish_reason: length). Raise Max Tokens in Settings.");
                }
                if (string.IsNullOrWhiteSpace(gPTOutputText))
                {
                    //an empty answer is an error, returning "" here left the bot saying nothing and
                    //crashing the chat post on an empty Twitch message body
                    _bBBlog.Error($"GPT returned no answer content (finish_reason: {finishReason}). The model probably spent its whole budget on reasoning. Enable 'No reasoning' in Settings or raise Max Tokens.");
                    return null;
                }
                //keep the conversation for context, the way the old Conversation object did
                _chatHistory.Add(new UserChatMessage(UserInput));
                _chatHistory.Add(new AssistantChatMessage(gPTOutputText));
                while (_chatHistory.Count > maxChatHistoryMessages)
                {
                    _chatHistory.RemoveAt(0);
                }
                _bBBlog.Info(gPTOutputText);
                _bBBlog.Info("GPT Response done");
                return gPTOutputText;
            }
            catch (System.Security.Authentication.AuthenticationException ex)
            {
                MessageBox.Show(ex.Message, "GPT API Auth error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _bBBlog.Error("GPT API Auth error: " + ex.Message);
                return null;
            }
            catch (ClientResultException ex)
            {
                MessageBox.Show(ex.Message, "GPT API error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _bBBlog.Error("GPT API error: " + ex.Status + " " + ex.Message);
                return null;
            }

        }

        public void ChatGPTChatReset()
        {
            if (_chatHistory.Count > 0)
            {
                _bBBlog.Info("ChatGPTChatReset called, forcefully resetting the history");
                _chatHistory.Clear();
            } else
            {
                _bBBlog.Debug("ChatGPTChatReset failed, chat was not initialized anyway");
            }
        }
        public OpenAI()
        {
        }



    }
}
