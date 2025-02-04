﻿
using FreneticUtilities.FreneticExtensions;
using FreneticUtilities.FreneticToolkit;
using Newtonsoft.Json.Linq;
using StableSwarmUI.Core;
using StableSwarmUI.Text2Image;
using StableSwarmUI.Utils;
using System.IO;

namespace StableSwarmUI.Builtin_ComfyUIBackend;

/// <summary>Helper class for generating ComfyUI workflows from input parameters.</summary>
public class WorkflowGenerator
{
    /// <summary>Represents a step in the workflow generation process.</summary>
    /// <param name="Action">The action to take.</param>
    /// <param name="Priority">The priority to apply it at.
    /// These are such from lowest to highest.
    /// "-10" is the priority of the first core pre-init,
    /// "0" is before final outputs,
    /// "10" is final output.</param>
    public record class WorkflowGenStep(Action<WorkflowGenerator> Action, double Priority);

    /// <summary>Callable steps for modifying workflows as they go.</summary>
    public static List<WorkflowGenStep> Steps = new();

    /// <summary>Callable steps for configuring model generation.</summary>
    public static List<WorkflowGenStep> ModelGenSteps = new();

    /// <summary>Can be set to globally block custom nodes, if needed.</summary>
    public static volatile bool RestrictCustomNodes = false;

    /// <summary>Register a new step to the workflow generator.</summary>
    public static void AddStep(Action<WorkflowGenerator> step, double priority)
    {
        Steps.Add(new(step, priority));
        Steps = Steps.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>Register a new step to the workflow generator.</summary>
    public static void AddModelGenStep(Action<WorkflowGenerator> step, double priority)
    {
        ModelGenSteps.Add(new(step, priority));
        ModelGenSteps = ModelGenSteps.OrderBy(s => s.Priority).ToList();
    }

    /// <summary>Lock for when ensuring the backend has valid models.</summary>
    public static LockObject ModelDownloaderLock = new();

    static WorkflowGenerator()
    {
        #region Model Loader
        AddStep(g =>
        {
            g.FinalLoadedModel = g.UserInput.Get(T2IParamTypes.Model);
            (g.FinalModel, g.FinalClip, g.FinalVae) = g.CreateStandardModelLoader(g.FinalLoadedModel, "Base", "4");
        }, -15);
        AddModelGenStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.VAE, out T2IModel vae))
            {
                string vaeNode = g.CreateNode("VAELoader", new JObject()
                {
                    ["vae_name"] = vae.ToString(g.ModelFolderFormat)
                });
                g.LoadingVAE = new() { $"{vaeNode}", 0 };
            }
        }, -15);
        AddModelGenStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.Loras, out List<string> loras))
            {
                List<string> weights = g.UserInput.Get(T2IParamTypes.LoraWeights);
                T2IModelHandler loraHandler = Program.T2IModelSets["LoRA"];
                for (int i = 0; i < loras.Count; i++)
                {
                    T2IModel lora = loraHandler.Models[loras[i]];
                    float weight = weights == null ? 1 : float.Parse(weights[i]);
                    string newId = g.CreateNode("LoraLoader", new JObject()
                    {
                        ["model"] = g.LoadingModel,
                        ["clip"] = g.LoadingClip,
                        ["lora_name"] = lora.ToString(g.ModelFolderFormat),
                        ["strength_model"] = weight,
                        ["strength_clip"] = weight
                    });
                    g.LoadingModel = new JArray() { $"{newId}", 0 };
                    g.LoadingClip = new JArray() { $"{newId}", 1 };
                }
            }
        }, -10);
        AddModelGenStep(g =>
        {
            string applyTo = g.UserInput.Get(T2IParamTypes.FreeUApplyTo, null);
            if (ComfyUIBackendExtension.FeaturesSupported.Contains("freeu") && applyTo is not null)
            {
                if (applyTo == "Both" || applyTo == g.LoadingModelType)
                {
                    string freeU = g.CreateNode("FreeU", new JObject()
                    {
                        ["model"] = g.LoadingModel,
                        ["b1"] = g.UserInput.Get(T2IParamTypes.FreeUBlock1),
                        ["b2"] = g.UserInput.Get(T2IParamTypes.FreeUBlock2),
                        ["s1"] = g.UserInput.Get(T2IParamTypes.FreeUSkip1),
                        ["s2"] = g.UserInput.Get(T2IParamTypes.FreeUSkip2)
                    });
                    g.LoadingModel = new() { $"{freeU}", 0 };
                }
            }
        }, -8);
        AddModelGenStep(g =>
        {
            if (g.UserInput.Get(T2IParamTypes.SeamlessTileable))
            {
                string tiling = g.CreateNode("SwarmModelTiling", new JObject()
                {
                    ["model"] = g.LoadingModel
                });
                g.LoadingModel = new() { $"{tiling}", 0 };
            }
        }, -6);
        AddModelGenStep(g =>
        {
            if (ComfyUIBackendExtension.FeaturesSupported.Contains("aitemplate") && g.UserInput.Get(ComfyUIBackendExtension.AITemplateParam))
            {
                string aitLoad = g.CreateNode("AITemplateLoader", new JObject()
                {
                    ["model"] = g.LoadingModel,
                    ["keep_loaded"] = "disable"
                });
                g.LoadingModel = new() { $"{aitLoad}", 0 };
            }
        }, -3);
        #endregion
        #region Base Image
        AddStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image img))
            {
                g.CreateLoadImageNode(img, "${initimage}", true, "15");
                g.CreateNode("VAEEncode", new JObject()
                {
                    ["pixels"] = new JArray() { "15", 0 },
                    ["vae"] = g.FinalVae
                }, "5");
                if (g.UserInput.TryGet(T2IParamTypes.MaskImage, out Image mask))
                {
                    string maskNode = g.CreateLoadImageNode(mask, "${maskimage}", true);
                    string maskImageNode = g.CreateNode("ImageToMask", new JObject()
                    {
                        ["image"] = new JArray() { maskNode, 0 },
                        ["channel"] = "red"
                    });
                    string appliedNode = g.CreateNode("SetLatentNoiseMask", new JObject()
                    {
                        ["samples"] = new JArray() { "5", 0 },
                        ["mask"] = new JArray() { maskImageNode, 0 }
                    });
                    g.FinalLatentImage = new JArray() { appliedNode, 0 };
                }
            }
            else
            {
                g.CreateNode("EmptyLatentImage", new JObject()
                {
                    ["batch_size"] = g.UserInput.Get(T2IParamTypes.BatchSize, 1),
                    ["height"] = g.UserInput.GetImageHeight(),
                    ["width"] = g.UserInput.Get(T2IParamTypes.Width)
                }, "5");
            }
        }, -9);
        #endregion
        #region Positive Prompt
        AddStep(g =>
        {
            g.FinalPrompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.Prompt), g.FinalClip, g.UserInput.Get(T2IParamTypes.Model), true);
        }, -8);
        #endregion
        #region ReVision/UnCLIP
        AddStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.PromptImages, out List<Image> images) && images.Any())
            {
                if (!g.UserInput.TryGet(T2IParamTypes.Model, out T2IModel model) || model.ModelClass is null || model.ModelClass.ID != "stable-diffusion-xl-v1-base")
                {
                    throw new InvalidDataException($"Model type must be stable-diffusion-xl-v1-base for ReVision (currently is {model?.ModelClass?.ID ?? "Unknown"})");
                }
                bool autoZero = g.UserInput.Get(T2IParamTypes.RevisionZeroPrompt, false);
                if ((g.UserInput.TryGet(T2IParamTypes.Prompt, out string promptText) && string.IsNullOrWhiteSpace(promptText)) || autoZero)
                {
                    string zeroed = g.CreateNode("ConditioningZeroOut", new JObject()
                    {
                        ["conditioning"] = g.FinalPrompt
                    });
                    g.FinalPrompt = new JArray() { $"{zeroed}", 0 };
                }
                if ((g.UserInput.TryGet(T2IParamTypes.NegativePrompt, out string negPromptText) && string.IsNullOrWhiteSpace(negPromptText)) || autoZero)
                {
                    string zeroed = g.CreateNode("ConditioningZeroOut", new JObject()
                    {
                        ["conditioning"] = g.FinalNegativePrompt
                    });
                    g.FinalNegativePrompt = new JArray() { $"{zeroed}", 0 };
                }
                string visModelName = "clip_vision_g.safetensors";
                if (g.UserInput.TryGet(T2IParamTypes.ReVisionModel, out T2IModel visionModel))
                {
                    visModelName = visionModel.ToString(g.ModelFolderFormat);
                }
                else
                {
                    string filePath = Utilities.CombinePathWithAbsolute(Program.ServerSettings.Paths.ModelRoot, Program.ServerSettings.Paths.SDClipVisionFolder, "clip_vision_g.safetensors");
                    if (!File.Exists(filePath))
                    {
                        lock (ModelDownloaderLock)
                        {
                            if (!File.Exists(filePath)) // Double-check in case another thread downloaded it
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                                Logs.Info($"Downloading clip_vision_g.safetensors to {filePath}...");
                                long total = 3_689_911_098L;
                                double nextPerc = 0.05;
                                Utilities.DownloadFile("https://huggingface.co/stabilityai/control-lora/resolve/main/revision/clip_vision_g.safetensors", filePath, (bytes) =>
                                {
                                    double perc = bytes / (double)total;
                                    if (perc >= nextPerc)
                                    {
                                        Logs.Info($"clip_vision_g.safetensors download at {perc * 100:0.0}%...");
                                        nextPerc = Math.Round(perc / 0.05) * 0.05 + 0.05;
                                    }
                                }).Wait();
                                Logs.Info($"Downloading complete, continuing.");
                            }
                        }
                    }
                }
                string visionLoader = g.CreateNode("CLIPVisionLoader", new JObject()
                {
                    ["clip_name"] = visModelName
                });
                for (int i = 0; i < images.Count; i++)
                {
                    string imageLoader = g.CreateLoadImageNode(images[i], "${promptimages." + i + "}", false);
                    string encoded = g.CreateNode("CLIPVisionEncode", new JObject()
                    {
                        ["clip_vision"] = new JArray() { $"{visionLoader}", 0 },
                        ["image"] = new JArray() { $"{imageLoader}", 0 }
                    });
                    string unclipped = g.CreateNode("unCLIPConditioning", new JObject()
                    {
                        ["conditioning"] = g.FinalPrompt,
                        ["clip_vision_output"] = new JArray() { $"{encoded}", 0 },
                        ["strength"] = g.UserInput.Get(T2IParamTypes.ReVisionStrength, 1),
                        ["noise_augmentation"] = 0
                    });
                    g.FinalPrompt = new JArray() { $"{unclipped}", 0 };
                }
                if (g.UserInput.TryGet(ComfyUIBackendExtension.UseIPAdapterForRevision, out string ipAdapter) && ipAdapter != "None")
                {
                    string lastImage = g.CreateLoadImageNode(images[0], "${promptimages.0}", true);
                    for (int i = 1; i < images.Count; i++)
                    {
                        string newImg = g.CreateLoadImageNode(images[i], "${promptimages." + i + "}", true);
                        lastImage = g.CreateNode("ImageBatch", new JObject()
                        {
                            ["image1"] = new JArray() { lastImage, 0 },
                            ["image2"] = new JArray() { newImg, 0 }
                        });
                    }
                    string ipAdapterNode = g.CreateNode("IPAdapter", new JObject()
                    {
                        ["model"] = g.FinalModel,
                        ["image"] = new JArray() { lastImage, 0 },
                        ["clip_vision"] = new JArray() { $"{visionLoader}", 0 },
                        ["weight"] = g.UserInput.Get(ComfyUIBackendExtension.IPAdapterWeight, 1),
                        ["model_name"] = ipAdapter,
                        ["dtype"] = "fp16" // TODO: ...???
                    });
                    g.FinalModel = new JArray() { $"{ipAdapterNode}", 0 };
                }
            }
        }, -7);
        #endregion
        #region Negative Prompt
        AddStep(g =>
        {
            g.FinalNegativePrompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.NegativePrompt), g.FinalClip, g.UserInput.Get(T2IParamTypes.Model), false);
        }, -7);
        #endregion
        #region ControlNet
        AddStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.ControlNetModel, out T2IModel controlModel)
                && g.UserInput.TryGet(T2IParamTypes.ControlNetStrength, out double controlStrength))
            {
                string imageInput = "${controlnetimageinput}";
                if (!g.UserInput.TryGet(T2IParamTypes.ControlNetImage, out Image img))
                {
                    if (!g.UserInput.TryGet(T2IParamTypes.InitImage, out img))
                    {
                        Logs.Verbose($"Following error relates to parameters: {g.UserInput.ToJSON().ToDenseDebugString()}");
                        throw new InvalidDataException("Must specify either a ControlNet Image, or Init image. Or turn off ControlNet if not wanted.");
                    }
                    imageInput = "${initimage}";
                }
                string imageNode = g.CreateLoadImageNode(img, imageInput, true);
                if (!g.UserInput.TryGet(ComfyUIBackendExtension.ControlNetPreprocessorParam, out string preprocessor))
                {
                    preprocessor = "none";
                    string wantedPreproc = controlModel.Metadata?.Preprocesor;
                    if (!string.IsNullOrWhiteSpace(wantedPreproc))
                    {
                        string[] procs = ComfyUIBackendExtension.ControlNetPreprocessors.Keys.ToArray();
                        bool getBestFor(string phrase)
                        {
                            string result = procs.FirstOrDefault(m => m.ToLowerFast().Contains(phrase.ToLowerFast()));
                            if (result is not null)
                            {
                                preprocessor = result;
                                return true;
                            }
                            return false;
                        }
                        if (wantedPreproc == "depth")
                        {
                            if (!getBestFor("midas-depthmap") && !getBestFor("depthmap") && !getBestFor("depth") && !getBestFor("midas") && !getBestFor("zoe") && !getBestFor("leres"))
                            {
                                throw new InvalidDataException("No preprocessor found for depth - please install a Comfy extension that adds eg MiDaS depthmap preprocessors, or select 'none' if using a manual depthmap");
                            }
                        }
                        else if (wantedPreproc == "canny")
                        {
                            if (!getBestFor("cannyedge") && !getBestFor("canny"))
                            {
                                preprocessor = "none";
                            }
                        }
                        else if (wantedPreproc == "sketch")
                        {
                            if (!getBestFor("sketch") && !getBestFor("lineart") && !getBestFor("scribble"))
                            {
                                preprocessor = "none";
                            }
                        }
                    }
                }
                if (preprocessor.ToLowerFast() != "none")
                {
                    JToken objectData = ComfyUIBackendExtension.ControlNetPreprocessors[preprocessor];
                    string preProcNode = g.CreateNode(preprocessor, (_, n) =>
                    {
                        n["inputs"] = new JObject()
                        {
                            ["image"] = new JArray() { $"{imageNode}", 0 }
                        };
                        foreach ((string key, JToken data) in (JObject)objectData["input"]["required"])
                        {
                            if (data.Count() == 2 && data[1] is JObject settings && settings.TryGetValue("default", out JToken defaultValue))
                            {
                                n["inputs"][key] = defaultValue;
                            }
                        }
                    });
                    g.NodeHelpers["controlnet_preprocessor"] = $"{preProcNode}";
                    if (g.UserInput.Get(T2IParamTypes.ControlNetPreviewOnly))
                    {
                        g.FinalImageOut = new() { $"{preProcNode}", 0 };
                    }
                    imageNode = preProcNode;
                }
                // TODO: Preprocessor
                string controlModelNode = g.CreateNode("ControlNetLoader", new JObject()
                {
                    ["control_net_name"] = controlModel.ToString(g.ModelFolderFormat)
                });
                string applyNode = g.CreateNode("ControlNetApply", new JObject()
                {
                    ["conditioning"] = g.FinalPrompt,
                    ["control_net"] = new JArray() { $"{controlModelNode}", 0 },
                    ["image"] = new JArray() { $"{imageNode}", 0 },
                    ["strength"] = controlStrength
                });
                g.FinalPrompt = new() { $"{applyNode}", 0 };
            }
        }, -6);
        #endregion
        #region Sampler
        AddStep(g =>
        {
            int steps = g.UserInput.Get(T2IParamTypes.Steps);
            int startStep = 0;
            int endStep = 10000;
            if (g.UserInput.TryGet(T2IParamTypes.InitImage, out Image _) && g.UserInput.TryGet(T2IParamTypes.InitImageCreativity, out double creativity))
            {
                startStep = (int)Math.Round(steps * (1 - creativity));
            }
            if (g.UserInput.TryGet(T2IParamTypes.RefinerMethod, out string method) && method == "StepSwap" && g.UserInput.TryGet(T2IParamTypes.RefinerControl, out double refinerControl))
            {
                endStep = (int)Math.Round(steps * (1 - refinerControl));
            }
            if (g.UserInput.TryGet(T2IParamTypes.EndStepsEarly, out double endEarly))
            {
                endStep = (int)(steps * (1 - endEarly));
            }
            g.CreateKSampler(g.FinalModel, g.FinalPrompt, g.FinalNegativePrompt, g.FinalLatentImage, steps, startStep, endStep,
                g.UserInput.Get(T2IParamTypes.Seed), g.UserInput.Get(T2IParamTypes.RefinerMethod, "none") == "StepSwapNoisy", true, "10");
        }, -5);
        #endregion
        #region Refiner
        AddStep(g =>
        {
            if (g.UserInput.TryGet(T2IParamTypes.RefinerMethod, out string method)
                && g.UserInput.TryGet(T2IParamTypes.RefinerControl, out double refinerControl)
                && g.UserInput.TryGet(ComfyUIBackendExtension.RefinerUpscaleMethod, out string upscaleMethod))
            {
                JArray origVae = g.FinalVae, prompt = g.FinalPrompt, negPrompt = g.FinalNegativePrompt;
                if (g.UserInput.TryGet(T2IParamTypes.RefinerModel, out T2IModel refineModel) && refineModel is not null)
                {
                    g.FinalLoadedModel = refineModel;
                    (g.FinalModel, g.FinalClip, g.FinalVae) = g.CreateStandardModelLoader(refineModel, "Refiner", "20");
                    prompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.Prompt), g.FinalClip, refineModel, true);
                    negPrompt = g.CreateConditioning(g.UserInput.Get(T2IParamTypes.NegativePrompt), g.FinalClip, refineModel, false);
                }
                bool doUspcale = g.UserInput.TryGet(T2IParamTypes.RefinerUpscale, out double refineUpscale) && refineUpscale > 1;
                // TODO: Better same-VAE check
                bool modelMustReencode = refineModel != null && (refineModel?.ModelClass?.ID != "stable-diffusion-xl-v1-refiner" || g.UserInput.Get(T2IParamTypes.Model).ModelClass?.ID != "stable-diffusion-xl-v1-base");
                bool doPixelUpscale = doUspcale && (upscaleMethod.StartsWith("pixel-") || upscaleMethod.StartsWith("model-"));
                if (modelMustReencode || doPixelUpscale)
                {
                    g.CreateVAEDecode(origVae, g.FinalSamples, "24");
                    string pixelsNode = "24";
                    if (doPixelUpscale)
                    {
                        if (upscaleMethod.StartsWith("pixel-"))
                        {
                            g.CreateNode("ImageScaleBy", new JObject()
                            {
                                ["image"] = new JArray() { "24", 0 },
                                ["upscale_method"] = upscaleMethod.After("pixel-"),
                                ["scale_by"] = refineUpscale
                            }, "26");
                        }
                        else
                        {
                            g.CreateNode("UpscaleModelLoader", new JObject()
                            {
                                ["model_name"] = upscaleMethod.After("model-")
                            }, "27");
                            g.CreateNode("ImageUpscaleWithModel", new JObject()
                            {
                                ["upscale_model"] = new JArray() { "27", 0 },
                                ["image"] = new JArray() { "24", 0 }
                            }, "28");
                            g.CreateNode("ImageScale", new JObject()
                            {
                                ["image"] = new JArray() { "28", 0 },
                                ["width"] = (int)Math.Round(g.UserInput.Get(T2IParamTypes.Width) * refineUpscale),
                                ["height"] = (int)Math.Round(g.UserInput.GetImageHeight() * refineUpscale),
                                ["upscale_method"] = "bilinear",
                                ["crop"] = "disabled"
                            }, "26");
                        }
                        pixelsNode = "26";
                        if (refinerControl <= 0)
                        {
                            g.FinalImageOut = new() { "26", 0 };
                            return;
                        }
                    }
                    g.CreateNode("VAEEncode", new JObject()
                    {
                        ["pixels"] = new JArray() { pixelsNode, 0 },
                        ["vae"] = g.FinalVae
                    }, "25");
                    g.FinalSamples = new() { "25", 0 };
                }
                if (doUspcale && upscaleMethod.StartsWith("latent-"))
                {
                    g.CreateNode("LatentUpscaleBy", new JObject()
                    {
                        ["samples"] = g.FinalSamples,
                        ["upscale_method"] = upscaleMethod.After("latent-"),
                        ["scale_by"] = refineUpscale
                    }, "26");
                    g.FinalSamples = new() { "26", 0 };
                }
                int steps = g.UserInput.Get(T2IParamTypes.Steps);
                g.CreateKSampler(g.FinalModel, prompt, negPrompt, g.FinalSamples, steps, (int)Math.Round(steps * (1 - refinerControl)), 10000,
                    g.UserInput.Get(T2IParamTypes.Seed) + 1, false, method != "StepSwapNoisy", "23");
                g.FinalSamples = new() { "23", 0 };
            }
            // TODO: Refiner
        }, -4);
        #endregion
        #region VAEDecode
        AddStep(g =>
        {
            g.CreateVAEDecode(g.FinalVae, g.FinalSamples, "8");
        }, 1);
        #endregion
        #region Segmentation Processing
        AddStep(g =>
        {
            PromptRegion.Part[] parts = new PromptRegion(g.UserInput.Get(T2IParamTypes.Prompt, "")).Parts.Where(p => p.Type == PromptRegion.PartType.Segment).ToArray();
            if (parts.Any())
            {
                PromptRegion negativeRegion = new(g.UserInput.Get(T2IParamTypes.NegativePrompt, ""));
                PromptRegion.Part[] negativeParts = negativeRegion.Parts.Where(p => p.Type == PromptRegion.PartType.Segment).ToArray();
                for (int i = 0; i < parts.Length; i++)
                {
                    PromptRegion.Part part = parts[i];
                    string segmentNode = g.CreateNode("SwarmClipSeg", new JObject()
                    {
                        ["images"] = g.FinalImageOut,
                        ["match_text"] = part.DataText,
                        ["threshold"] = part.Strength
                    });
                    string blurNode = g.CreateNode("SwarmMaskBlur", new JObject()
                    {
                        ["mask"] = new JArray() { segmentNode, 0 },
                        ["blur_radius"] = 10,
                        ["sigma"] = 1
                    });
                    string growNode = g.CreateNode("GrowMask", new JObject()
                    {
                        ["mask"] = new JArray() { blurNode, 0 },
                        ["expand"] = 16,
                        ["tapered_corners"] = true
                    });
                    string boundsNode = g.CreateNode("SwarmMaskBounds", new JObject()
                    {
                        ["mask"] = new JArray() { growNode, 0 },
                        ["grow"] = 8
                    });
                    string croppedImage = g.CreateNode("SwarmImageCrop", new JObject()
                    {
                        ["image"] = g.FinalImageOut,
                        ["x"] = new JArray() { boundsNode, 0 },
                        ["y"] = new JArray() { boundsNode, 1 },
                        ["width"] = new JArray() { boundsNode, 2 },
                        ["height"] = new JArray() { boundsNode, 3 }
                    });
                    string croppedMask = g.CreateNode("CropMask", new JObject()
                    {
                        ["mask"] = new JArray() { growNode, 0 },
                        ["x"] = new JArray() { boundsNode, 0 },
                        ["y"] = new JArray() { boundsNode, 1 },
                        ["width"] = new JArray() { boundsNode, 2 },
                        ["height"] = new JArray() { boundsNode, 3 }
                    });
                    string scaledImage = g.CreateNode("SwarmImageScaleForMP", new JObject()
                    {
                        ["image"] = new JArray() { croppedImage, 0 },
                        ["width"] = g.UserInput.Get(T2IParamTypes.Width, 1024),
                        ["height"] = g.UserInput.GetImageHeight(),
                        ["can_shrink"] = false
                    });
                    string vaeEncoded = g.CreateNode("VAEEncode", new JObject()
                    {
                        ["pixels"] = new JArray() { scaledImage, 0 },
                        ["vae"] = g.FinalVae
                    });
                    string masked = g.CreateNode("SetLatentNoiseMask", new JObject()
                    {
                        ["samples"] = new JArray() { vaeEncoded, 0 },
                        ["mask"] = new JArray() { croppedMask, 0 }
                    });
                    JArray prompt = g.CreateConditioning(part.Prompt, g.FinalClip, g.FinalLoadedModel, true);
                    string neg = negativeParts.FirstOrDefault(p => p.DataText == part.DataText)?.Prompt ?? negativeRegion.GlobalPrompt;
                    JArray negPrompt = g.CreateConditioning(neg, g.FinalClip, g.FinalLoadedModel, false);
                    int steps = g.UserInput.Get(T2IParamTypes.Steps);
                    int startStep = (int)Math.Round(steps * (1 - part.Strength2));
                    long seed = g.UserInput.Get(T2IParamTypes.Seed) + 2 + i;
                    string sampler = g.CreateKSampler(g.FinalModel, prompt, negPrompt, new() { masked, 0 }, steps, startStep, 10000, seed, false, true);
                    string decoded = g.CreateNode("VAEDecode", new JObject()
                    {
                        ["samples"] = new JArray() { sampler, 0 },
                        ["vae"] = g.FinalVae
                    });
                    string scaledBack = g.CreateNode("ImageScale", new JObject()
                    {
                        ["image"] = new JArray() { decoded, 0 },
                        ["width"] = new JArray() { boundsNode, 2 },
                        ["height"] = new JArray() { boundsNode, 3 },
                        ["upscale_method"] = "bilinear",
                        ["crop"] = "disabled"
                    });
                    string composited = g.CreateNode("ImageCompositeMasked", new JObject()
                    {
                        ["destination"] = g.FinalImageOut,
                        ["source"] = new JArray() { scaledBack, 0 },
                        ["mask"] = new JArray() { croppedMask, 0 },
                        ["x"] = new JArray() { boundsNode, 0 },
                        ["y"] = new JArray() { boundsNode, 1 },
                        ["resize_source"] = false
                    });
                    g.FinalImageOut = new() { composited, 0 };
                }
            }
        }, 5);
        #endregion
        #region SaveImage
        AddStep(g =>
        {
            // Should already be set correct, but make sure (in case eg overwritten by refiner)
            if (g.UserInput.Get(T2IParamTypes.ControlNetPreviewOnly) && g.NodeHelpers.TryGetValue("controlnet_preprocessor", out string preproc))
            {
                g.FinalImageOut = new() { preproc, 0 };
            }
            if (ComfyUIBackendExtension.FeaturesSupported.Contains("comfy_saveimage_ws") && !RestrictCustomNodes)
            {
                g.CreateNode("SwarmSaveImageWS", new JObject()
                {
                    ["images"] = g.FinalImageOut
                }, "9");
            }
            else
            {
                g.CreateNode("SaveImage", new JObject()
                {
                    ["filename_prefix"] = $"StableSwarmUI_{Random.Shared.Next():X4}_",
                    ["images"] = g.FinalImageOut
                }, "9");
            }
        }, 10);
        #endregion
    }

    /// <summary>The raw user input data.</summary>
    public T2IParamInput UserInput;

    /// <summary>The output workflow object.</summary>
    public JObject Workflow;

    /// <summary>Lastmost node ID for key input trackers.</summary>
    public JArray FinalModel = new() { "4", 0 },
        FinalClip = new() { "4", 1 },
        FinalVae = new() { "4", 2 },
        FinalLatentImage = new() { "5", 0 },
        FinalPrompt = new() { "6", 0 },
        FinalNegativePrompt = new() { "7", 0 },
        FinalSamples = new() { "10", 0 },
        FinalImageOut = new() { "8", 0 },
        LoadingModel = null, LoadingClip = null, LoadingVAE = null;

    /// <summary>What model currently matches <see cref="FinalModel"/>.</summary>
    public T2IModel FinalLoadedModel;

    /// <summary>Mapping of any extra nodes to keep track of, Name->ID, eg "MyNode" -> "15".</summary>
    public Dictionary<string, string> NodeHelpers = new();

    /// <summary>Last used ID, tracked to safely add new nodes with sequential IDs. Note that this starts at 100, as below 100 is reserved for constant node IDs.</summary>
    public int LastID = 100;

    /// <summary>Model folder separator format, if known.</summary>
    public string ModelFolderFormat;

    /// <summary>Type id ('Base', 'Refiner') of the current loading model.</summary>
    public string LoadingModelType;

    /// <summary>Creates a new node with the given class type and configuration action, and optional manual ID.</summary>
    public string CreateNode(string classType, Action<string, JObject> configure, string id = null)
    {
        id ??= $"{LastID++}";
        JObject obj = new() { ["class_type"] = classType };
        configure(id, obj);
        Workflow[id] = obj;
        return id;
    }

    /// <summary>Creates a new node with the given class type and input data, and optional manual ID.</summary>
    public string CreateNode(string classType, JObject input, string id = null)
    {
        return CreateNode(classType, (_, n) => n["inputs"] = input, id);
    }

    /// <summary>Creates a new node to load an image.</summary>
    public string CreateLoadImageNode(Image img, string param, bool resize, string nodeId = null)
    {
        if (ComfyUIBackendExtension.FeaturesSupported.Contains("comfy_loadimage_b64") && !RestrictCustomNodes)
        {
            return CreateNode("SwarmLoadImageB64", new JObject()
            {
                ["image_base64"] = (resize ? img.Resize(UserInput.Get(T2IParamTypes.Width), UserInput.GetImageHeight()) : img).AsBase64
            }, nodeId);
        }
        else
        {
            return CreateNode("LoadImage", new JObject()
            {
                ["image"] = param
            }, nodeId);
        }
    }

    /// <summary>Call to run the generation process and get the result.</summary>
    public JObject Generate()
    {
        Workflow = new();
        foreach (WorkflowGenStep step in Steps)
        {
            step.Action(this);
        }
        return Workflow;
    }

    /// <summary>Creates a model loader and adapts it with any registered model adapters, and returns (Model, Clip, VAE).</summary>
    public (JArray, JArray, JArray) CreateStandardModelLoader(T2IModel model, string type, string id = null)
    {
        LoadingModelType = type;
        string modelNode = CreateNode("CheckpointLoaderSimple", new JObject()
        {
            ["ckpt_name"] = model.ToString(ModelFolderFormat)
        }, id);
        LoadingModel = new() { modelNode, 0 };
        LoadingClip = new() { modelNode, 1 };
        LoadingVAE = new() { modelNode, 2 };
        foreach (WorkflowGenStep step in ModelGenSteps)
        {
            step.Action(this);
        }
        return (LoadingModel, LoadingClip, LoadingVAE);
    }

    /// <summary>Creates a VAEDecode node and returns its node ID.</summary>
    public string CreateVAEDecode(JArray vae, JArray latent, string id = null)
    {
        if (UserInput.TryGet(T2IParamTypes.VAETileSize, out int tileSize))
        {
            return CreateNode("VAEDecodeTiled", new JObject()
            {
                ["vae"] = vae,
                ["samples"] = latent,
                ["tile_size"] = tileSize
            }, id);
        }
        string className = UserInput.Get(T2IParamTypes.SeamlessTileable) ? "SwarmTileableVAEDecode" : "VAEDecode";
        return CreateNode(className, new JObject()
        {
            ["vae"] = vae,
            ["samples"] = latent
        }, id);
    }

    /// <summary>Creates a KSampler and returns its node ID.</summary>
    public string CreateKSampler(JArray model, JArray pos, JArray neg, JArray latent, int steps, int startStep, int endStep, long seed, bool returnWithLeftoverNoise, bool addNoise, string id = null)
    {
        JObject inputs = new()
        {
            ["model"] = model,
            ["noise_seed"] = seed,
            ["steps"] = steps,
            ["cfg"] = UserInput.Get(T2IParamTypes.CFGScale),
            // TODO: proper sampler input, and intelligent default scheduler per sampler
            ["sampler_name"] = UserInput.Get(ComfyUIBackendExtension.SamplerParam, "euler"),
            ["scheduler"] = UserInput.Get(ComfyUIBackendExtension.SchedulerParam, "normal"),
            ["positive"] = pos,
            ["negative"] = neg,
            ["latent_image"] = latent,
            ["start_at_step"] = startStep,
            ["end_at_step"] = endStep,
            ["return_with_leftover_noise"] = returnWithLeftoverNoise ? "enable" : "disable",
            ["add_noise"] = addNoise ? "enable" : "disable"
        };
        if (ComfyUIBackendExtension.FeaturesSupported.Contains("variation_seed") && !RestrictCustomNodes)
        {
            inputs["var_seed"] = UserInput.Get(T2IParamTypes.VariationSeed, 0);
            inputs["var_seed_strength"] = UserInput.Get(T2IParamTypes.VariationSeedStrength, 0);
            inputs["sigma_min"] = UserInput.Get(T2IParamTypes.SamplerSigmaMin, -1);
            inputs["sigma_max"] = UserInput.Get(T2IParamTypes.SamplerSigmaMax, -1);
            inputs["rho"] = UserInput.Get(T2IParamTypes.SamplerRho, 7);
            inputs["previews"] = "default";
            return CreateNode("SwarmKSampler", inputs, id);
        }
        else
        {
            return CreateNode("KSamplerAdvanced", inputs, id);
        }
    }

    /// <summary>Creates a "CLIPTextEncode" or equivalent node for the given input.</summary>
    public JArray CreateConditioningDirect(string prompt, JArray clip, T2IModel model, bool isPositive)
    {
        string node;
        if (model is not null && model.ModelClass is not null && model.ModelClass.ID == "stable-diffusion-xl-v1-base")
        {
            double mult = isPositive ? 1.5 : 0.8;
            int width = UserInput.Get(T2IParamTypes.Width, 1024);
            int height = UserInput.GetImageHeight();
            node = CreateNode("CLIPTextEncodeSDXL", new JObject()
            {
                ["clip"] = clip,
                ["text_g"] = prompt,
                ["text_l"] = prompt,
                ["crop_w"] = 0,
                ["crop_h"] = 0,
                ["width"] = (int)(width * mult),
                ["height"] = (int)(height * mult),
                ["target_width"] = width,
                ["target_height"] = height
            });
        }
        else
        {
            node = CreateNode("CLIPTextEncode", new JObject()
            {
                ["clip"] = clip,
                ["text"] = prompt
            });
        }
        return new() { node, 0 };
    }

    public record struct RegionHelper(JArray PartCond, JArray Mask);

    /// <summary>Creates a "CLIPTextEncode" or equivalent node for the given input, applying prompt-given conditioning modifiers as relevant.</summary>
    public JArray CreateConditioning(string prompt, JArray clip, T2IModel model, bool isPositive)
    {
        PromptRegion regionalizer = new(prompt);
        JArray globalCond = CreateConditioningDirect(regionalizer.GlobalPrompt, clip, model, isPositive);
        PromptRegion.Part[] parts = regionalizer.Parts.Where(p => p.Type != PromptRegion.PartType.Segment).ToArray();
        if (parts.IsEmpty())
        {
            return globalCond;
        }
        double globalStrength = UserInput.Get(T2IParamTypes.GlobalRegionFactor, 0.5);
        List<RegionHelper> regions = new();
        JArray lastMergedMask = null;
        foreach (PromptRegion.Part part in parts)
        {
            JArray partCond = CreateConditioningDirect(part.Prompt, clip, model, isPositive);
            string regionNode = CreateNode("SwarmSquareMaskFromPercent", new JObject()
            {
                ["x"] = part.X,
                ["y"] = part.Y,
                ["width"] = part.Width,
                ["height"] = part.Height,
                ["strength"] = part.Strength
            });
            RegionHelper region = new(partCond, new() { regionNode, 0 });
            regions.Add(region);
            if (lastMergedMask is null)
            {
                lastMergedMask = region.Mask;
            }
            else
            {
                string overlapped = CreateNode("SwarmOverMergeMasksForOverlapFix", new JObject()
                {
                    ["mask_a"] = lastMergedMask,
                    ["mask_b"] = region.Mask
                });
                lastMergedMask = new() { overlapped, 0 };
            }
        }
        string globalMask = CreateNode("SwarmSquareMaskFromPercent", new JObject()
        {
            ["x"] = 0,
            ["y"] = 0,
            ["width"] = 1,
            ["height"] = 1,
            ["strength"] = 1
        });
        string maskBackground = CreateNode("SwarmExcludeFromMask", new JObject()
        {
            ["main_mask"] = new JArray() { globalMask, 0 },
            ["exclude_mask"] = lastMergedMask
        });
        string backgroundPrompt = string.IsNullOrWhiteSpace(regionalizer.BackgroundPrompt) ? regionalizer.GlobalPrompt : regionalizer.BackgroundPrompt;
        JArray backgroundCond = CreateConditioningDirect(backgroundPrompt, clip, model, isPositive);
        string mainConditioning = CreateNode("ConditioningSetMask", new JObject()
        {
            ["conditioning"] = backgroundCond,
            ["mask"] = new JArray() { maskBackground, 0 },
            ["strength"] = 1 - globalStrength,
            ["set_cond_area"] = "default"
        });
        DebugMask(new() { maskBackground, 0 });
        void DebugMask(JArray mask)
        {
            if (UserInput.Get(ComfyUIBackendExtension.DebugRegionalPrompting))
            {
                string imgNode = CreateNode("MaskToImage", new JObject()
                {
                    ["mask"] = mask
                });
                CreateNode("SwarmSaveImageWS", new JObject()
                {
                    ["images"] = new JArray() { imgNode, 0 }
                });
            }
        }
        foreach (RegionHelper region in regions)
        {
            string overlapped = CreateNode("SwarmCleanOverlapMasksExceptSelf", new JObject()
            {
                ["mask_self"] = region.Mask,
                ["mask_merged"] = lastMergedMask
            });
            DebugMask(new() { overlapped, 0 });
            string regionCond = CreateNode("ConditioningSetMask", new JObject()
            {
                ["conditioning"] = region.PartCond,
                ["mask"] = new JArray() { overlapped, 0 },
                ["strength"] = 1 - globalStrength,
                ["set_cond_area"] = "default"
            });
            mainConditioning = CreateNode("ConditioningCombine", new JObject()
            {
                ["conditioning_1"] = new JArray() { mainConditioning, 0 },
                ["conditioning_2"] = new JArray() { regionCond, 0 }
            });
        }
        string globalCondApplied = CreateNode("ConditioningSetMask", new JObject()
        {
            ["conditioning"] = globalCond,
            ["mask"] = new JArray() { globalMask, 0 },
            ["strength"] = globalStrength,
            ["set_cond_area"] = "default"
        });
        string finalCond = CreateNode("ConditioningCombine", new JObject()
        {
            ["conditioning_1"] = new JArray() { mainConditioning, 0 },
            ["conditioning_2"] = new JArray() { globalCondApplied, 0 }
        });
        return new(finalCond, 0);
    }
}
