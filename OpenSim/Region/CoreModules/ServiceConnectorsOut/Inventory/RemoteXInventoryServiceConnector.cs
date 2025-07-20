/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using log4net;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Mono.Addins;
using Nini.Config;
using OpenSim.Framework;
using OpenSim.Framework.Monitoring;
using OpenSim.Services.Connectors;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "RemoteXInventoryServicesConnector")]
    public class RemoteXInventoryServicesConnector : ISharedRegionModule, IInventoryService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// Scene used by this module.  This currently needs to be publicly settable for HGInventoryBroker.
        /// </summary>
        public Scene Scene { get; set; }

        private bool m_Enabled;
        private XInventoryServicesConnector m_RemoteConnector;

        private IUserManagement m_UserManager;
        public IUserManagement UserManager
        {
            get
            {
                if (m_UserManager == null)
                {
                    m_UserManager = Scene.RequestModuleInterface<IUserManagement>();

                    if (m_UserManager == null)
                        m_log.ErrorFormat(
                            "[XINVENTORY CONNECTOR]: Could not retrieve IUserManagement module from {0}",
                            Scene.RegionInfo.RegionName);
                }

                return m_UserManager;
            }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "RemoteXInventoryServicesConnector"; }
        }

        public RemoteXInventoryServicesConnector()
        {
        }

        public RemoteXInventoryServicesConnector(string url)
        {
            m_RemoteConnector = new XInventoryServicesConnector(url);
        }

        public RemoteXInventoryServicesConnector(IConfigSource source)
        {
            Init(source);
        }

        protected void Init(IConfigSource source)
        {
            m_RemoteConnector = new XInventoryServicesConnector(source);
        }

        #region ISharedRegionModule

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("InventoryServices", "");
                if (name == Name)
                {
                    Init(source);
                    m_Enabled = true;

                    m_log.Info("[XINVENTORY CONNECTOR]: Remote XInventory enabled");
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
//            m_Scene = scene;
            //m_log.Debug("[XXXX] Adding scene " + m_Scene.RegionInfo.RegionName);

            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IInventoryService>(this);

            if (Scene == null)
                Scene = scene;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_log.InfoFormat("[XINVENTORY CONNECTOR]: Enabled remote XInventory for region {0}", scene.RegionInfo.RegionName);

        }

        #endregion ISharedRegionModule

        #region IInventoryService

        public  bool CreateUserInventory(UUID user)
        {
            return false;
        }

        public  List<InventoryFolderBase> GetInventorySkeleton(UUID userId)
        {
            return m_RemoteConnector.GetInventorySkeleton(userId);
        }

        public InventoryFolderBase GetRootFolder(UUID userID)
        {
            return m_RemoteConnector.GetRootFolder(userID);
        }

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            return m_RemoteConnector.GetFolderForType(userID, type);
        }

        public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            InventoryCollection invCol = m_RemoteConnector.GetFolderContent(userID, folderID);

            // Commenting this for now, because it's causing more grief than good
            //if (invCol != null && UserManager != null)
            //{
            //    // Protect ourselves against the caller subsequently modifying the items list
            //    List<InventoryItemBase> items = new List<InventoryItemBase>(invCol.Items);

            //    if (items != null && items.Count > 0)
            //        //Util.FireAndForget(delegate
            //        //{
            //            foreach (InventoryItemBase item in items)
            //                if (!string.IsNullOrEmpty(item.CreatorData))
            //                    UserManager.AddUser(item.CreatorIdAsUuid, item.CreatorData);
            //        //});
            //}

            return invCol;
        }

        public virtual InventoryCollection[] GetMultipleFoldersContent(UUID principalID, UUID[] folderIDs)
        {
            return m_RemoteConnector.GetMultipleFoldersContent(principalID, folderIDs);
        }

        public  List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            return m_RemoteConnector.GetFolderItems(userID, folderID);
        }

        public  bool AddFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            return m_RemoteConnector.AddFolder(folder);
        }

        public  bool UpdateFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            return m_RemoteConnector.UpdateFolder(folder);
        }

        public  bool MoveFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            return m_RemoteConnector.MoveFolder(folder);
        }

        public  bool DeleteFolders(UUID ownerID, List<UUID> folderIDs)
        {
            if (folderIDs == null)
                return false;
            if (folderIDs.Count == 0)
                return false;

            return m_RemoteConnector.DeleteFolders(ownerID, folderIDs);
        }


        public  bool PurgeFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            return m_RemoteConnector.PurgeFolder(folder);
        }

        public  bool AddItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            return m_RemoteConnector.AddItem(item);
        }

        public  bool UpdateItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            return m_RemoteConnector.UpdateItem(item);
        }

        public  bool MoveItems(UUID ownerID, List<InventoryItemBase> items)
        {
            if (items == null)
                return false;

            return m_RemoteConnector.MoveItems(ownerID, items);
        }


        public  bool DeleteItems(UUID ownerID, List<UUID> itemIDs)
        {
            if (itemIDs == null)
                return false;
            if (itemIDs.Count == 0)
                return true;

            return m_RemoteConnector.DeleteItems(ownerID, itemIDs);
        }

        public  InventoryItemBase GetItem(UUID userID, UUID itemID)
        {
            //m_log.DebugFormat("[XINVENTORY CONNECTOR]: GetItem {0}", item.ID);

            if (m_RemoteConnector == null)
                m_log.DebugFormat("[XINVENTORY CONNECTOR]: connector stub is null!!!");
            return m_RemoteConnector.GetItem(userID, itemID);
        }

        public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] itemIDs)
        {
            if (itemIDs == null)
                return new InventoryItemBase[0];

            return m_RemoteConnector.GetMultipleItems(userID, itemIDs);
        }

        public  InventoryFolderBase GetFolder(UUID userID, UUID folderID)
        {
            //m_log.DebugFormat("[XINVENTORY CONNECTOR]: GetFolder {0}", folder.ID);

            return m_RemoteConnector.GetFolder(userID, folderID);
        }

        public  bool HasInventoryForUser(UUID userID)
        {
            return false;
        }

        public  List<InventoryItemBase> GetActiveGestures(UUID userId)
        {
            return new List<InventoryItemBase>();
        }

        public  int GetAssetPermissions(UUID userID, UUID assetID)
        {
            return m_RemoteConnector.GetAssetPermissions(userID, assetID);
        }

        // Async methods - delegate to remote connector
        public async Task<bool> CreateUserInventoryAsync(UUID user)
        {
            return await m_RemoteConnector.CreateUserInventoryAsync(user);
        }

        public async Task<List<InventoryFolderBase>> GetInventorySkeletonAsync(UUID userId)
        {
            return await m_RemoteConnector.GetInventorySkeletonAsync(userId);
        }

        public async Task<InventoryFolderBase> GetRootFolderAsync(UUID userID)
        {
            return await m_RemoteConnector.GetRootFolderAsync(userID);
        }

        public async Task<InventoryFolderBase> GetFolderForTypeAsync(UUID userID, FolderType type)
        {
            return await m_RemoteConnector.GetFolderForTypeAsync(userID, type);
        }

        public async Task<InventoryCollection> GetFolderContentAsync(UUID userID, UUID folderID)
        {
            return await m_RemoteConnector.GetFolderContentAsync(userID, folderID);
        }

        public async Task<InventoryCollection[]> GetMultipleFoldersContentAsync(UUID principalID, UUID[] folderIDs)
        {
            return await m_RemoteConnector.GetMultipleFoldersContentAsync(principalID, folderIDs);
        }

        public async Task<List<InventoryItemBase>> GetFolderItemsAsync(UUID userID, UUID folderID)
        {
            return await m_RemoteConnector.GetFolderItemsAsync(userID, folderID);
        }

        public async Task<bool> AddFolderAsync(InventoryFolderBase folder)
        {
            return await m_RemoteConnector.AddFolderAsync(folder);
        }

        public async Task<bool> UpdateFolderAsync(InventoryFolderBase folder)
        {
            return await m_RemoteConnector.UpdateFolderAsync(folder);
        }

        public async Task<bool> MoveFolderAsync(InventoryFolderBase folder)
        {
            return await m_RemoteConnector.MoveFolderAsync(folder);
        }

        public async Task<bool> DeleteFoldersAsync(UUID ownerID, List<UUID> folderIDs)
        {
            return await m_RemoteConnector.DeleteFoldersAsync(ownerID, folderIDs);
        }

        public async Task<bool> PurgeFolderAsync(InventoryFolderBase folder)
        {
            return await m_RemoteConnector.PurgeFolderAsync(folder);
        }

        public async Task<bool> AddItemAsync(InventoryItemBase item)
        {
            return await m_RemoteConnector.AddItemAsync(item);
        }

        public async Task<bool> UpdateItemAsync(InventoryItemBase item)
        {
            return await m_RemoteConnector.UpdateItemAsync(item);
        }

        public async Task<bool> MoveItemsAsync(UUID ownerID, List<InventoryItemBase> items)
        {
            return await m_RemoteConnector.MoveItemsAsync(ownerID, items);
        }

        public async Task<bool> DeleteItemsAsync(UUID ownerID, List<UUID> itemIDs)
        {
            return await m_RemoteConnector.DeleteItemsAsync(ownerID, itemIDs);
        }

        public async Task<InventoryItemBase> GetItemAsync(UUID userID, UUID itemID)
        {
            return await m_RemoteConnector.GetItemAsync(userID, itemID);
        }

        public async Task<InventoryItemBase[]> GetMultipleItemsAsync(UUID userID, UUID[] itemIDs)
        {
            return await m_RemoteConnector.GetMultipleItemsAsync(userID, itemIDs);
        }

        public async Task<InventoryFolderBase> GetFolderAsync(UUID userID, UUID folderID)
        {
            return await m_RemoteConnector.GetFolderAsync(userID, folderID);
        }

        public async Task<bool> HasInventoryForUserAsync(UUID userID)
        {
            return await m_RemoteConnector.HasInventoryForUserAsync(userID);
        }

        public async Task<List<InventoryItemBase>> GetActiveGesturesAsync(UUID userId)
        {
            return await Task.FromResult(new List<InventoryItemBase>());
        }

        public async Task<int> GetAssetPermissionsAsync(UUID userID, UUID assetID)
        {
            return await m_RemoteConnector.GetAssetPermissionsAsync(userID, assetID);
        }

        #endregion
    }
}
