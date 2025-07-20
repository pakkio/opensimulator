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
using Mono.Addins;
using Nini.Config;
using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Tasks;
using OpenSim.Framework;

using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Connectors;
using OpenMetaverse;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Inventory
{
    [Extension(Path = "/OpenSim/RegionModules", NodeName = "RegionModule", Id = "HGInventoryBroker")]
    public class HGInventoryBroker : ISharedRegionModule, IInventoryService
    {
        private static readonly ILog m_log = LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private static bool m_Enabled = false;

        private const int CONNECTORS_CACHE_EXPIRE = 60000; // 1 minute

        private readonly List<Scene> m_Scenes = new List<Scene>();

        private IInventoryService m_LocalGridInventoryService;
        private readonly ExpiringCacheOS<string, IInventoryService> m_connectors = new ExpiringCacheOS<string, IInventoryService>();

        // A cache of userIDs --> ServiceURLs, for HGBroker only
        protected readonly ConcurrentDictionary<UUID, string> m_InventoryURLs = new ConcurrentDictionary<UUID, string>();
        private readonly InventoryCache m_Cache = new InventoryCache();

        /// <summary>
        /// Used to serialize inventory requests.
        /// </summary>
        private readonly object m_Lock = new object();

        protected IUserManagement m_UserManagement;
        protected IUserManagement UserManagementModule
        {
            get
            {
                if (m_UserManagement == null)
                {
                    m_UserManagement = m_Scenes[0].RequestModuleInterface<IUserManagement>();

                    if (m_UserManagement == null)
                        m_log.ErrorFormat(
                            "[HG INVENTORY CONNECTOR]: Could not retrieve IUserManagement module from {0}",
                            m_Scenes[0].RegionInfo.RegionName);
                }

                return m_UserManagement;
            }
        }

        public Type ReplaceableInterface
        {
            get { return null; }
        }

        public string Name
        {
            get { return "HGInventoryBroker"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("InventoryServices", string.Empty);
                if (name == Name)
                {
                    IConfig inventoryConfig = source.Configs["InventoryService"];
                    if (inventoryConfig == null)
                    {
                        m_log.Error("[HG INVENTORY CONNECTOR]: InventoryService missing from OpenSim.ini");
                        return;
                    }

                    string localDll = inventoryConfig.GetString("LocalGridInventoryService", string.Empty);
 
                    if (localDll.Length == 0)
                    {
                        m_log.Error("[HG INVENTORY CONNECTOR]: No LocalGridInventoryService named in section InventoryService");
                        //return;
                        throw new Exception("Unable to proceed. Please make sure your ini files in config-include are updated according to .example's");
                    }

                    m_LocalGridInventoryService =
                            ServerUtils.LoadPlugin<IInventoryService>(localDll, new object[] { source });

                    if (m_LocalGridInventoryService == null)
                    {
                        m_log.Error("[HG INVENTORY CONNECTOR]: Can't load local inventory service");
                        return;
                    }

                    m_Enabled = true;
                    m_log.InfoFormat("[HG INVENTORY CONNECTOR]: HG inventory broker enabled with inner connector of type {0}", m_LocalGridInventoryService.GetType());
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
            if (!m_Enabled)
                return;

            m_Scenes.Add(scene);

            scene.RegisterModuleInterface<IInventoryService>(this);

            if (m_Scenes.Count == 1)
            {
                // FIXME: The local connector needs the scene to extract the UserManager.  However, it's not enabled so
                // we can't just add the region.  But this approach is super-messy.
                if (m_LocalGridInventoryService is RemoteXInventoryServicesConnector)
                {
                    m_log.DebugFormat(
                        "[HG INVENTORY BROKER]: Manually setting scene in RemoteXInventoryServicesConnector to {0}",
                        scene.RegionInfo.RegionName);

                    ((RemoteXInventoryServicesConnector)m_LocalGridInventoryService).Scene = scene;
                }
                else if (m_LocalGridInventoryService is LocalInventoryServicesConnector)
                {
                    m_log.DebugFormat(
                        "[HG INVENTORY BROKER]: Manually setting scene in LocalInventoryServicesConnector to {0}",
                        scene.RegionInfo.RegionName);

                    ((LocalInventoryServicesConnector)m_LocalGridInventoryService).Scene = scene;
                }
            }
            scene.EventManager.OnClientClosed += OnClientClosed;
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_Scenes.Remove(scene);
            scene.EventManager.OnClientClosed -= OnClientClosed;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            m_log.InfoFormat("[HG INVENTORY CONNECTOR]: Enabled HG inventory for region {0}", scene.RegionInfo.RegionName);

        }

        #region URL Cache

        void OnClientClosed(UUID clientID, Scene scene)
        {
            foreach (Scene s in m_Scenes)
            {
                if(s.TryGetScenePresence(clientID, out ScenePresence sp) && !sp.IsChildAgent && sp.ControllingClient != null && sp.ControllingClient.IsActive)
                {
                    //m_log.DebugFormat("[HG INVENTORY CACHE]: OnClientClosed in {0}, but user {1} still in sim. Keeping inventoryURL in cache",
                    //        scene.RegionInfo.RegionName, clientID);
                    return;
                }
            }

            m_InventoryURLs.TryRemove(clientID, out string _);
            m_Cache.RemoveAll(clientID);
        }

        /// <summary>
        /// Gets the user's inventory URL from its serviceURLs, if the user is foreign,
        /// and sticks it in the cache
        /// </summary>
        /// <param name="userID"></param>
        private string CacheInventoryServiceURL(UUID userID)
        {
            if (UserManagementModule != null && !UserManagementModule.IsLocalGridUser(userID))
            {
                // The user is not local; let's cache its service URL
                string inventoryURL;
                ScenePresence sp = null;
                foreach (Scene scene in m_Scenes)
                {
                    if(scene.TryGetScenePresence(userID, out sp))
                    {
                        AgentCircuitData aCircuit = scene.AuthenticateHandler.GetAgentCircuitData(sp.ControllingClient.CircuitCode);
                        if (aCircuit is null)
                            return null;
                        if (aCircuit.ServiceURLs is null)
                            return null;

                        if (aCircuit.ServiceURLs.TryGetValue("InventoryServerURI", out object otmp))
                        {
                            inventoryURL = otmp.ToString();
                            if (!string.IsNullOrEmpty(inventoryURL))
                            {
                                inventoryURL = inventoryURL.Trim('/');
                                m_InventoryURLs[userID] = inventoryURL;
                                //m_log.DebugFormat("[HG INVENTORY CONNECTOR]: Added {0} to the cache of inventory URLs", inventoryURL);
                                return inventoryURL;
                            }
                        }
                    }
                }
                if (sp is null)
                {
                    inventoryURL = UserManagementModule.GetUserServerURL(userID, "InventoryServerURI");
                    if (!string.IsNullOrEmpty(inventoryURL))
                    {
                        inventoryURL = inventoryURL.Trim('/');
                        m_InventoryURLs[userID] = inventoryURL;
                        //m_log.DebugFormat("[HG INVENTORY CONNECTOR]: Added {0} to the cache of inventory URLs", inventoryURL);
                        return inventoryURL;
                    }
                }
            }
            return null;
        }

        public string GetInventoryServiceURL(UUID userID)
        {
            if (m_InventoryURLs.TryGetValue(userID, out string value))
                return value;

             return CacheInventoryServiceURL(userID);
        }

        #endregion

        #region IInventoryService

        public bool CreateUserInventory(UUID userID)
        {
            return m_LocalGridInventoryService.CreateUserInventory(userID);
        }

        public List<InventoryFolderBase> GetInventorySkeleton(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                lock (m_Lock)
                    return m_LocalGridInventoryService.GetInventorySkeleton(userID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetInventorySkeleton(userID);
        }

        public InventoryFolderBase GetRootFolder(UUID userID)
        {
            //m_log.DebugFormat("[HG INVENTORY CONNECTOR]: GetRootFolder for {0}", userID);
            InventoryFolderBase root = m_Cache.GetRootFolder(userID);
            if (root is not null)
                return root;

            string invURL = GetInventoryServiceURL(userID);
            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetRootFolder(userID);

            IInventoryService connector = GetConnector(invURL);

            root = connector.GetRootFolder(userID);

            m_Cache.Cache(userID, root);

            return root;
        }

        public InventoryFolderBase GetFolderForType(UUID userID, FolderType type)
        {
            //m_log.DebugFormat("[HG INVENTORY CONNECTOR]: GetFolderForType {0} type {1}", userID, type);
            InventoryFolderBase f = m_Cache.GetFolderForType(userID, type);
            if (f != null)
                return f;

            string invURL = GetInventoryServiceURL(userID);
            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetFolderForType(userID, type);

            IInventoryService connector = GetConnector(invURL);

            f = connector.GetFolderForType(userID, type);

            m_Cache.Cache(userID, type, f);

            return f;
        }

        public InventoryCollection GetFolderContent(UUID userID, UUID folderID)
        {
            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderContent " + folderID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetFolderContent(userID, folderID);

            InventoryCollection c = m_Cache.GetFolderContent(userID, folderID);
            if (c != null)
            {
                m_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderContent found content in cache " + folderID);
                return c;
            }

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolderContent(userID, folderID);
        }

        public InventoryCollection[] GetMultipleFoldersContent(UUID userID, UUID[] folderIDs)
        {
            string invURL = GetInventoryServiceURL(userID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetMultipleFoldersContent(userID, folderIDs);

            InventoryCollection[] coll = new InventoryCollection[folderIDs.Length];
            int i = 0;
            foreach (UUID fid in folderIDs)
                coll[i++] = GetFolderContent(userID, fid);

            return coll;
        }

        public List<InventoryItemBase> GetFolderItems(UUID userID, UUID folderID)
        {
            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderItems " + folderID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetFolderItems(userID, folderID);

            List<InventoryItemBase> items = m_Cache.GetFolderItems(userID, folderID);
            if (items != null)
            {
                m_log.Debug("[HG INVENTORY CONNECTOR]: GetFolderItems found items in cache " + folderID);
                return items;
            }

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolderItems(userID, folderID);
        }

        public bool AddFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: AddFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.AddFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.AddFolder(folder);
        }

        public bool UpdateFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: UpdateFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.UpdateFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.UpdateFolder(folder);
        }

        public bool DeleteFolders(UUID ownerID, List<UUID> folderIDs)
        {
            if (folderIDs == null)
                return false;
            if (folderIDs.Count == 0)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: DeleteFolders for " + ownerID);

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.DeleteFolders(ownerID, folderIDs);

            IInventoryService connector = GetConnector(invURL);

            return connector.DeleteFolders(ownerID, folderIDs);
        }

        public bool MoveFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: MoveFolder for " + folder.Owner);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.MoveFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.MoveFolder(folder);
        }

        public bool PurgeFolder(InventoryFolderBase folder)
        {
            if (folder == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: PurgeFolder for " + folder.Owner);

            string invURL = GetInventoryServiceURL(folder.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.PurgeFolder(folder);

            IInventoryService connector = GetConnector(invURL);

            return connector.PurgeFolder(folder);
        }

        public bool AddItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: AddItem " + item.ID);

            string invURL = GetInventoryServiceURL(item.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.AddItem(item);

            IInventoryService connector = GetConnector(invURL);

            return connector.AddItem(item);
        }

        public bool UpdateItem(InventoryItemBase item)
        {
            if (item == null)
                return false;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: UpdateItem " + item.ID);

            string invURL = GetInventoryServiceURL(item.Owner);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.UpdateItem(item);

            IInventoryService connector = GetConnector(invURL);

            return connector.UpdateItem(item);
        }

        public bool MoveItems(UUID ownerID, List<InventoryItemBase> items)
        {
            if (items == null)
                return false;
            if (items.Count == 0)
                return true;

            //m_log.Debug("[HG INVENTORY CONNECTOR]: MoveItems for " + ownerID);

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.MoveItems(ownerID, items);

            IInventoryService connector = GetConnector(invURL);

            return connector.MoveItems(ownerID, items);
        }

        public bool DeleteItems(UUID ownerID, List<UUID> itemIDs)
        {
            //m_log.DebugFormat("[HG INVENTORY CONNECTOR]: Delete {0} items for user {1}", itemIDs.Count, ownerID);

            if (itemIDs == null)
                return false;
            if (itemIDs.Count == 0)
                return true;

            string invURL = GetInventoryServiceURL(ownerID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.DeleteItems(ownerID, itemIDs);

            IInventoryService connector = GetConnector(invURL);

            return connector.DeleteItems(ownerID, itemIDs);
        }

        public InventoryItemBase GetItem(UUID principalID, UUID itemID)
        {
            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetItem " + item.ID);

            string invURL = GetInventoryServiceURL(principalID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetItem(principalID, itemID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetItem(principalID, itemID);
        }

        public InventoryItemBase[] GetMultipleItems(UUID userID, UUID[] itemIDs)
        {
            if (itemIDs is null || itemIDs.Length == 0)
                return Array.Empty<InventoryItemBase>();

            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetMultipleItems " + item.ID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetMultipleItems(userID, itemIDs);

            IInventoryService connector = GetConnector(invURL);
            if (connector is null)
                return Array.Empty<InventoryItemBase>();
            return connector.GetMultipleItems(userID, itemIDs);
        }

        public InventoryFolderBase GetFolder(UUID principalID, UUID folderID)
        {
            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetFolder " + folder.ID);

            string invURL = GetInventoryServiceURL(principalID);

            if (invURL is null) // not there, forward to local inventory connector to resolve
                return m_LocalGridInventoryService.GetFolder(principalID, folderID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetFolder(principalID, folderID);
        }

        public bool HasInventoryForUser(UUID userID)
        {
            return false;
        }

        public List<InventoryItemBase> GetActiveGestures(UUID userId)
        {
            return new List<InventoryItemBase>();
        }

        public int GetAssetPermissions(UUID userID, UUID assetID)
        {
            //m_log.Debug("[HG INVENTORY CONNECTOR]: GetAssetPermissions " + assetID);

            string invURL = GetInventoryServiceURL(userID);

            if (invURL == null) // not there, forward to local inventory connector to resolve
                lock (m_Lock)
                    return m_LocalGridInventoryService.GetAssetPermissions(userID, assetID);

            IInventoryService connector = GetConnector(invURL);

            return connector.GetAssetPermissions(userID, assetID);
        }

        // Async methods - delegate to appropriate connector
        public async Task<bool> CreateUserInventoryAsync(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.CreateUserInventoryAsync(userID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.CreateUserInventoryAsync(userID);
        }

        public async Task<List<InventoryFolderBase>> GetInventorySkeletonAsync(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetInventorySkeletonAsync(userID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetInventorySkeletonAsync(userID);
        }

        public async Task<InventoryFolderBase> GetRootFolderAsync(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetRootFolderAsync(userID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetRootFolderAsync(userID);
        }

        public async Task<InventoryFolderBase> GetFolderForTypeAsync(UUID userID, FolderType type)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetFolderForTypeAsync(userID, type);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetFolderForTypeAsync(userID, type);
        }

        public async Task<InventoryCollection> GetFolderContentAsync(UUID userID, UUID folderID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetFolderContentAsync(userID, folderID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetFolderContentAsync(userID, folderID);
        }

        public async Task<InventoryCollection[]> GetMultipleFoldersContentAsync(UUID userID, UUID[] folderIDs)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetMultipleFoldersContentAsync(userID, folderIDs);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetMultipleFoldersContentAsync(userID, folderIDs);
        }

        public async Task<List<InventoryItemBase>> GetFolderItemsAsync(UUID userID, UUID folderID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetFolderItemsAsync(userID, folderID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetFolderItemsAsync(userID, folderID);
        }

        public async Task<bool> AddFolderAsync(InventoryFolderBase folder)
        {
            string invURL = GetInventoryServiceURL(folder.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.AddFolderAsync(folder);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.AddFolderAsync(folder);
        }

        public async Task<bool> UpdateFolderAsync(InventoryFolderBase folder)
        {
            string invURL = GetInventoryServiceURL(folder.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.UpdateFolderAsync(folder);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.UpdateFolderAsync(folder);
        }

        public async Task<bool> MoveFolderAsync(InventoryFolderBase folder)
        {
            string invURL = GetInventoryServiceURL(folder.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.MoveFolderAsync(folder);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.MoveFolderAsync(folder);
        }

        public async Task<bool> DeleteFoldersAsync(UUID ownerID, List<UUID> folderIDs)
        {
            string invURL = GetInventoryServiceURL(ownerID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.DeleteFoldersAsync(ownerID, folderIDs);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.DeleteFoldersAsync(ownerID, folderIDs);
        }

        public async Task<bool> PurgeFolderAsync(InventoryFolderBase folder)
        {
            string invURL = GetInventoryServiceURL(folder.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.PurgeFolderAsync(folder);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.PurgeFolderAsync(folder);
        }

        public async Task<bool> AddItemAsync(InventoryItemBase item)
        {
            string invURL = GetInventoryServiceURL(item.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.AddItemAsync(item);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.AddItemAsync(item);
        }

        public async Task<bool> UpdateItemAsync(InventoryItemBase item)
        {
            string invURL = GetInventoryServiceURL(item.Owner);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.UpdateItemAsync(item);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.UpdateItemAsync(item);
        }

        public async Task<bool> MoveItemsAsync(UUID ownerID, List<InventoryItemBase> items)
        {
            string invURL = GetInventoryServiceURL(ownerID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.MoveItemsAsync(ownerID, items);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.MoveItemsAsync(ownerID, items);
        }

        public async Task<bool> DeleteItemsAsync(UUID ownerID, List<UUID> itemIDs)
        {
            string invURL = GetInventoryServiceURL(ownerID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.DeleteItemsAsync(ownerID, itemIDs);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.DeleteItemsAsync(ownerID, itemIDs);
        }

        public async Task<InventoryItemBase> GetItemAsync(UUID principalID, UUID itemID)
        {
            string invURL = GetInventoryServiceURL(principalID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetItemAsync(principalID, itemID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetItemAsync(principalID, itemID);
        }

        public async Task<InventoryItemBase[]> GetMultipleItemsAsync(UUID userID, UUID[] itemIDs)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetMultipleItemsAsync(userID, itemIDs);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetMultipleItemsAsync(userID, itemIDs);
        }

        public async Task<InventoryFolderBase> GetFolderAsync(UUID principalID, UUID folderID)
        {
            string invURL = GetInventoryServiceURL(principalID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetFolderAsync(principalID, folderID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetFolderAsync(principalID, folderID);
        }

        public async Task<bool> HasInventoryForUserAsync(UUID userID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.HasInventoryForUserAsync(userID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.HasInventoryForUserAsync(userID);
        }

        public async Task<List<InventoryItemBase>> GetActiveGesturesAsync(UUID userId)
        {
            return await Task.FromResult(new List<InventoryItemBase>());
        }

        public async Task<int> GetAssetPermissionsAsync(UUID userID, UUID assetID)
        {
            string invURL = GetInventoryServiceURL(userID);
            if (invURL == null)
            {
                IInventoryService localService;
                lock (m_Lock)
                    localService = m_LocalGridInventoryService;
                return await localService.GetAssetPermissionsAsync(userID, assetID);
            }
            IInventoryService connector = GetConnector(invURL);
            return await connector.GetAssetPermissionsAsync(userID, assetID);
        }

        #endregion

        private IInventoryService GetConnector(string url)
        {
            IInventoryService connector = null;
            lock (m_connectors)
            {
                if (!m_connectors.TryGetValue(url, out connector))
                {
                    // Still not as flexible as I would like this to be,
                    // but good enough for now
                    RemoteXInventoryServicesConnector rxisc = new RemoteXInventoryServicesConnector(url);
                    rxisc.Scene = m_Scenes[0];
                    connector = rxisc;
                }
                if (connector != null)
                    m_connectors.AddOrUpdate(url, connector, CONNECTORS_CACHE_EXPIRE);
            }
            return connector;
        }
    }
}
