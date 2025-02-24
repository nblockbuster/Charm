﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Arithmic;
using HelixToolkit.SharpDX.Core.Model.Scene;
using Internal.Fbx;
using Tiger;
using Tiger.Exporters;
using Tiger.Schema;
using Tiger.Schema.Entity;
using Tiger.Schema.Investment;

namespace Charm;

public partial class EntityView : UserControl
{
    public FileHash Hash;
    private string Name;

    public EntityView()
    {
        InitializeComponent();
    }

    public bool LoadEntity(FileHash entityHash, FbxHandler fbxHandler)
    {
        fbxHandler.Clear();
        Entity entity = FileResourcer.Get().GetFile<Entity>(entityHash);
        AddEntity(entity, ModelView.GetSelectedLod(), fbxHandler);
        return LoadUI(fbxHandler);
    }

    public async void LoadEntityFromApi(TigerHash apiHash, FbxHandler fbxHandler)
    {
        fbxHandler.Clear();
        List<Entity> entities = Investment.Get().GetEntitiesFromHash(apiHash);
        foreach (var entity in entities)
        {
            // todo find out why sometimes this is null
            if (entity == null)
            {
                continue;
            }
            AddEntity(entity, ModelView.GetSelectedLod(), fbxHandler);
        }
        LoadUI(fbxHandler);
    }

    private void AddEntity(Entity entity, ExportDetailLevel detailLevel, FbxHandler fbxHandler)
    {
        var dynamicParts = entity.Load(detailLevel);
        ModelView.SetGroupIndices(new HashSet<int>(dynamicParts.Select(x => x.GroupIndex)));
        dynamicParts = dynamicParts.Where(x => x.GroupIndex == ModelView.GetSelectedGroupIndex()).ToList();
        fbxHandler.AddEntityToScene(entity, dynamicParts, detailLevel);
        Log.Verbose($"Adding entity {entity.Hash}/{entity.Model?.Hash} with {dynamicParts.Sum(p => p.Indices.Count)} vertices to fbx");
    }

    private bool LoadUI(FbxHandler fbxHandler)
    {
        MainViewModel MVM = (MainViewModel)ModelView.UCModelView.Resources["MVM"];
        ConfigSubsystem config = CharmInstance.GetSubsystem<ConfigSubsystem>();
        string filePath = $"{config.GetExportSavePath()}/temp.fbx";
        fbxHandler.ExportScene(filePath);
        bool loaded = MVM.LoadEntityFromFbx(filePath);
        fbxHandler.Clear();
        return loaded;
    }

    public static void Export(List<Entity> entities, string name, ExportTypeFlag exportType, EntitySkeleton overrideSkeleton = null)
    {
        FbxHandler fbxHandler = new FbxHandler(exportType == ExportTypeFlag.Full);
        ConfigSubsystem config = CharmInstance.GetSubsystem<ConfigSubsystem>();

        List<FbxNode> boneNodes = null;
        if (overrideSkeleton != null)
            boneNodes = fbxHandler.AddSkeleton(overrideSkeleton.GetBoneNodes());

        Log.Verbose($"Exporting entity model name: {name}");
        string savePath = config.GetExportSavePath();
        string meshName = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        if (exportType == ExportTypeFlag.Full)
        {
            savePath += $"/{meshName}";
        }
        Directory.CreateDirectory(savePath);

        foreach (var entity in entities)
        {
            var dynamicParts = entity.Load(ExportDetailLevel.MostDetailed);
            fbxHandler.AddEntityToScene(entity, dynamicParts, ExportDetailLevel.MostDetailed, boneNodes);
            if (exportType == ExportTypeFlag.Full)
            {
                entity.SaveMaterialsFromParts(savePath, dynamicParts, config.GetUnrealInteropEnabled() || config.GetS2ShaderExportEnabled());
                entity.SaveTexturePlates(savePath);
            }
        }

        if (exportType == ExportTypeFlag.Full)
        {
            fbxHandler.InfoHandler.SetMeshName(meshName);
            if (config.GetUnrealInteropEnabled())
            {
                fbxHandler.InfoHandler.SetUnrealInteropPath(config.GetUnrealInteropPath());
                AutomatedExporter.SaveInteropUnrealPythonFile(savePath, meshName, AutomatedExporter.ImportType.Entity, config.GetOutputTextureFormat());
            }
            if (config.GetBlenderInteropEnabled())
            {
                AutomatedExporter.SaveInteropBlenderPythonFile(savePath, meshName, AutomatedExporter.ImportType.Entity, config.GetOutputTextureFormat());
            }
        }

        // Scale and rotate
        // fbxHandler.ScaleAndRotateForBlender(boneNodes[0]);
        fbxHandler.InfoHandler.AddType("Entity");
        fbxHandler.ExportScene($"{savePath}/{meshName}.fbx");
        fbxHandler.Dispose();
        Log.Info($"Exported entity model {name} to {savePath.Replace('\\', '/')}/");
    }

    public static void ExportInventoryItem(ApiItem item)
    {
        string name = string.Join("_", $"{item.Item.TagData.InventoryItemHash}_{item.ItemName}"
            .Split(Path.GetInvalidFileNameChars()));
        // Export the model
        // todo bad, should be replaced
        EntitySkeleton overrideSkeleton;
        if (Strategy.CurrentStrategy == TigerStrategy.DESTINY2_WITCHQUEEN_6307)
        {
            overrideSkeleton = new EntitySkeleton(new FileHash("BC38AB80"));
        }
        // todo do DESTINY2_LATEST
        else
        {
            overrideSkeleton = null;
        }
        var val = Investment.Get().GetPatternEntityFromHash(item.Item.TagData.InventoryItemHash);
        // var resource = (D2Class_6E358080)val.PatternAudio.TagData.Unk18;
        // if (resource.PatternAudioGroups[0].WeaponSkeletonEntity != null)
        // {
        // overrideSkeleton = resource.PatternAudioGroups[0].WeaponSkeletonEntity.Skeleton;
        // }
        if (val != null && val.Skeleton != null)
        {
            overrideSkeleton = val.Skeleton;
        }
        EntityView.Export(Investment.Get().GetEntitiesFromHash(item.Item.TagData.InventoryItemHash),
            name, ExportTypeFlag.Full, overrideSkeleton);

        // Export the dye info
        Dictionary<TigerHash, Dye> dyes = new Dictionary<TigerHash, Dye>();
        if (item.Item.TagData.Unk90.GetValue(item.Item.GetReader()) is D2Class_77738080 translationBlock)
        {
            foreach (var dyeEntry in translationBlock.DefaultDyes)
            {
                Dye dye = Investment.Get().GetDyeFromIndex(dyeEntry.DyeIndex);
                dyes.Add(Investment.Get().GetChannelHashFromIndex(dyeEntry.ChannelIndex), dye);
            }
            foreach (var dyeEntry in translationBlock.LockedDyes)
            {
                Dye dye = Investment.Get().GetDyeFromIndex(dyeEntry.DyeIndex);
                dyes.Add(Investment.Get().GetChannelHashFromIndex(dyeEntry.ChannelIndex), dye);
            }
        }

        ConfigSubsystem config = CharmInstance.GetSubsystem<ConfigSubsystem>();
        string savePath = config.GetExportSavePath();
        string meshName = name;
        savePath += $"/{meshName}";
        Directory.CreateDirectory(savePath);
        AutomatedExporter.SaveBlenderApiFile(savePath, string.Join("_", item.ItemName.Split(Path.GetInvalidFileNameChars())),
            config.GetOutputTextureFormat(), dyes.Values.ToList());
    }
}
