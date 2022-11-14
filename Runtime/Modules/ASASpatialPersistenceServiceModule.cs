// Copyright (c) Reality Collective. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Azure.SpatialAnchors;
using Microsoft.Azure.SpatialAnchors.Unity;
using RealityCollective.Extensions;
using RealityCollective.ServiceFramework.Attributes;
using RealityCollective.ServiceFramework.Definitions;
using RealityCollective.ServiceFramework.Definitions.Platforms;
using RealityCollective.Utilities.Async;
using RealityToolkit.SpatialPersistence;
using RealityToolkit.SpatialPersistence.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

namespace RealityToolkit.Modules.SpatialPersistence
{
    [RuntimePlatform(typeof(AndroidPlatform))]
    [RuntimePlatform(typeof(UniversalWindowsPlatform))]
    [System.Runtime.InteropServices.Guid("02963BCE-8519-4923-AE59-833953F6F13C")]
    public class ASASpatialPersistenceServiceModule : BaseSpatialPersistenceServiceModule, ISpatialPersistenceServiceModule
    {
        #region Private Properties
        private readonly ISpatialPersistenceService spatialPersistenceSystem = null;
        private SpatialAnchorManager cloudManager;
        private AnchorLocateCriteria anchorLocateCriteria;
        private CloudSpatialAnchorWatcher currentWatcher;
        private Dictionary<Guid, CloudSpatialAnchor> detectedAnchors = new Dictionary<Guid, CloudSpatialAnchor>();

        private SpatialAnchorManager CloudManager
        {
            get
            {
                if (cloudManager == null)
                {
                    // Get a reference to the SpatialAnchorManager component (must be on the same gameobject)
                    cloudManager = GameObject.FindObjectOfType<SpatialAnchorManager>();
                    if (cloudManager.IsNull())
                    {
                        var ARSessionOrigin = GameObject.FindObjectOfType<ARSessionOrigin>();
                        if (ARSessionOrigin.IsNotNull())
                        {
                            cloudManager = ARSessionOrigin.gameObject.AddComponent<SpatialAnchorManager>();
                        }

                        if (cloudManager == null)
                        {
                            var message = $"Unable to locate either the {typeof(SpatialAnchorManager)} or {typeof(ARSession)} in the scene, service cannot initialize";
                            OnSpatialPersistenceError(message);
                        }
                    }
                }
                return cloudManager;
            }
        }
        #endregion Private Properties

        #region Constructor
        public ASASpatialPersistenceServiceModule(string name, uint priority, BaseProfile profile, ISpatialPersistenceService parentService)
            : base(name, priority, null, parentService)
        {
            spatialPersistenceSystem = parentService;
        }
        #endregion Constructor

        #region BaseService Implementation
        /// <inheritdoc />
        public override void Destroy()
        {
            if (Application.isPlaying)
            {
                if (IsRunning)
                {
                    CloudManager.DestroySession();
                }

                if (currentWatcher != null)
                {
                    currentWatcher.Stop();
                    currentWatcher = null;
                }
            }

            base.Destroy();

        }
        #endregion BaseService Implementation

        #region ISpatialPersistenceServiceModule Implementation
        /// <inheritdoc />
        public override bool IsRunning => CloudManager != null && CloudManager.IsSessionStarted;

        /// <inheritdoc />
        public override SpatialPersistenceTrackingType TrackingType => SpatialPersistenceTrackingType.CloudAnchor;

        /// <inheritdoc />
        public override async Task StartSpatialPersistenceModule()
        {
#if UNITY_WSA
            await StartASASession();
#else
            if (ARSession.state == ARSessionState.SessionTracking)
            {
                await StartASASession();
            }
            else
            {
                ARSession.stateChanged += ARSession_stateChanged;
            }
#endif
        }

        /// <inheritdoc />
        public override async void StopSpatialPersistenceModule()
        {
            if (CloudManager == null) { return; }

            if (currentWatcher != null)
            {
                currentWatcher.Stop();
                currentWatcher = null;
            }

            // Stops any existing session
            CloudManager.StopSession();

            // Resets the current session if there is one, and waits for any active queries to be stopped
            await CloudManager.ResetSessionAsync();
            OnSessionEnded();
        }

        /// <inheritdoc />
        public override async void TryCreateAnchor(Vector3 position, Quaternion rotation, DateTimeOffset timeToLive)
            => await TryCreateAnchorAsync(position, rotation, timeToLive);

        /// <inheritdoc />
        public override async Task<Guid> TryCreateAnchorAsync(Vector3 position, Quaternion rotation, DateTimeOffset timeToLive)
        {
            try
            {
                await this.WaitUntil(_ => CloudManager != null);
            }
            catch (Exception)
            {
                const string errorMessage = "Unable to create Anchor as the Azure Spatial Anchors CloudManager is not running, is it configured correctly?";
                OnSpatialPersistenceError(errorMessage);
                return Guid.Empty;
            }

            try
            {
                await this.WaitUntil(_ => CloudManager.IsSessionStarted == true);
            }
            catch (Exception)
            {
                const string errorMessage = "The cloud session hasn't been started!";
                OnSpatialPersistenceError(errorMessage);
                return Guid.Empty;
            }

            OnCreateAnchorStarted();

            var anchoredObject = new GameObject(nameof(CloudNativeAnchor));
            anchoredObject.transform.SetPositionAndRotation(position, rotation);

            var cloudNativeAnchor = anchoredObject.EnsureComponent<CloudNativeAnchor>();

            // If the cloud portion of the anchor hasn't been created yet, create it
            if (cloudNativeAnchor.CloudAnchor == null)
            {
                await cloudNativeAnchor.NativeToCloud();
            }

            // Get the cloud portion of the anchor
            CloudSpatialAnchor cloudAnchor = cloudNativeAnchor.CloudAnchor;

            // if a positive expiration date is provided, then this is applied to the Anchor, else it will not be set and become a permenant anchor until deleted
            if (DateTimeOffset.Compare(timeToLive, DateTime.Now) > 0)
            {
                cloudAnchor.Expiration = timeToLive;
            }

            while (!CloudManager.IsReadyForCreate)
            {
                await Awaiters.UnityMainThread;
                OnSpatialPersistenceStatusMessage($"{CloudManager.SessionStatus.RecommendedForCreateProgress}");
            }

            try
            {
                await CloudManager.CreateAnchorAsync(cloudAnchor);

                if (cloudAnchor != null &&
                    Guid.TryParse(cloudAnchor.Identifier, out var cloudAnchorGuid))
                {
                    detectedAnchors.Add(cloudAnchorGuid, cloudAnchor);
                    anchoredObject.name = $"Cloud Anchor [{cloudAnchor.Identifier}]";
                    OnCreateAnchorSucceeded(cloudAnchorGuid, anchoredObject);
                    return cloudAnchorGuid;
                }
            }
            catch (Exception e)
            {
                OnSpatialPersistenceError($"{e}");
            }

            Debug.LogError("Failed to create anchor!");
            OnCreateAnchorFailed();
            cloudAnchor = null;
            return Guid.Empty;
        }

        /// <inheritdoc />
        public override async void TryFindAnchors(params Guid[] ids)
            => await TryFindAnchorsAsync(ids);

        /// <inheritdoc />
        public override async Task<bool> TryFindAnchorsAsync(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length > 0, "No Ids found to locate");

            try
            {
                await this.WaitUntil(_ => CloudManager != null, -1);
            }
            catch (Exception)
            {
                const string errorMessage = "Unable to create Anchor as the Azure Spatial Anchors CloudManager is not running, is it configured correctly?";
                OnSpatialPersistenceError(errorMessage);
                return false;
            }

            try
            {
                await this.WaitUntil(_ => CloudManager.IsSessionStarted == true, -1);
            }
            catch (Exception)
            {
                const string errorMessage = "The cloud session hasn't been started!";
                OnSpatialPersistenceError(errorMessage);
                return false;
            }

            try
            {
                await this.WaitUntil(_ => anchorLocateCriteria != null, -1);
            }
            catch (Exception)
            {
                const string errorMessage = "No Anchor criteria was found!";
                OnSpatialPersistenceError(errorMessage);
                return false;
            }

            if (ids.Length > 0)
            {
                if (currentWatcher != null)
                {
                    currentWatcher.Stop();
                }

                if (CloudManager != null &&
                    CloudManager.Session != null &&
                    anchorLocateCriteria != null)
                {
                    OnFindAnchorStarted();
                    anchorLocateCriteria.Identifiers = ids.ToStringArray();
                    currentWatcher = CloudManager.Session.CreateWatcher(anchorLocateCriteria);
                    return true;
                }

                Debug.LogError("Failed to start anchor search!");
                currentWatcher = null;
            }
            else
            {
                Debug.LogWarning("No ids passed");
            }

            return false;
        }

        /// <inheritdoc />
        public override bool HasAnchor(GameObject anchoredObject)
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var cloudAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();
            return cloudAnchor != null && !string.IsNullOrEmpty(cloudAnchor.CloudAnchor.Identifier);
        }

        /// <inheritdoc />
        public override bool TryMoveAnchor(GameObject anchoredObject, Vector3 position, Quaternion rotation, Guid cloudAnchorID = new Guid())
        {
            Debug.Assert(anchoredObject != null, "Anchored Object is null");

            var attachedAnchor = anchoredObject.GetComponent<CloudNativeAnchor>();

            if (attachedAnchor == null)
            {
                Debug.Assert(attachedAnchor != null, $"No existing {nameof(CloudNativeAnchor)} to move");
                return false;
            }

            //If the Azure Spatial Anchors CloudManager is not running, expose an error.
            if (cloudAnchorID != Guid.Empty && (CloudManager == null || !CloudManager.IsSessionStarted))
            {
                const string errorMessage = "Unable to create Anchor as the Azure Spatial Anchors CloudManager is not running, is it configured correctly?";
                OnSpatialPersistenceError(errorMessage);
                return false;
            }

            // if a Cloud identifier is provided and the corresponding ID has been found, move object to anchored point.
            // Else force move the anchor which breaks any preexisting cloud anchor reference.
            if (cloudAnchorID != Guid.Empty && detectedAnchors.ContainsKey(cloudAnchorID))
            {
                attachedAnchor.CloudToNative(detectedAnchors[cloudAnchorID]);
            }
            else
            {
                attachedAnchor.SetPose(position, rotation);
            }

            OnAnchorUpdated(cloudAnchorID, anchoredObject);
            return true;
        }

        /// <inheritdoc />
        public override async void DeleteAnchors(params Guid[] ids)
        {
            Debug.Assert(ids != null, "ID array is null");
            Debug.Assert(ids.Length > 0, "No Ids found to delete");

            if (ids.Length > 0)
            {
                for (int i = 0; i < ids.Length; i++)
                {
                    if (detectedAnchors.ContainsKey(ids[i]))
                    {
                        await CloudManager.DeleteAnchorAsync(detectedAnchors[ids[i]]);
                        detectedAnchors.Remove(ids[i]);
                        OnAnchorDeleted(ids[i]);
                    }
                }
            }
        }

        /// <inheritdoc />
        public override bool TryClearAnchorCache()
        {
            detectedAnchors.Clear();
            return detectedAnchors?.Count == 0;
        }

        #endregion ISpatialPersistenceServiceModule Implementation

        #region Private Methods
        /// <summary>
        /// Internal method to start the Azure Spatial Anchors module
        /// </summary>
        /// <returns></returns>
        private async Task StartASASession()
        {
            if (CloudManager == null)
            {
                var message = $"Unable to locate either the {typeof(SpatialAnchorManager)} or {typeof(ARSession)} in the scene, service cannot initialize";
                OnSpatialPersistenceError(message);
                return;
            }

            await Awaiters.UnityMainThread;

            if (CloudManager.Session == null)
            {
                // Creates a new session if one does not exist
                await CloudManager.CreateSessionAsync();
            }

            await CloudManager.StartSessionAsync();

            if (CloudManager.Session != null && CloudManager.IsSessionStarted)
            {
                anchorLocateCriteria = new AnchorLocateCriteria();

                // Register for Azure Spatial Anchor events
                CloudManager.AnchorLocated += CloudManager_AnchorLocated;
                OnSessionStarted();
                Debug.Log($"{nameof(ISpatialPersistenceServiceModule)}.{nameof(StartASASession)} successful");
            }
            else
            {
                const string errorMessage = "Unable to start the Azure Spatial Anchors CloudManager, is it configured correctly?";
                OnSpatialPersistenceError(errorMessage);
            }

        }

        /// <summary>
        /// For Android we need to wait until the ARSession has started before initialising Anchors
        /// </summary>
        /// <param name="obj"></param>
        private async void ARSession_stateChanged(ARSessionStateChangedEventArgs obj)
        {
            if (obj.state == ARSessionState.SessionTracking && !IsRunning)
            {
                await StartASASession();
            }
        }

        /// <summary>
        /// Anchor located by the ASA Cloud watcher service, returns the ID reported by the service for the anchor via <see cref="AnchorLocated"/> event
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="args"></param>
        private void CloudManager_AnchorLocated(object sender, AnchorLocatedEventArgs args)
        {
            try
            {
                if (Guid.TryParse(args.Identifier, out var anchorGuid))
                {
                    // If an anchor is found but has no Anchor data, create a new CloudSpatialAnchor
                    if (!detectedAnchors.ContainsKey(anchorGuid))
                    {
                        detectedAnchors.Add(anchorGuid, args.Anchor);
                    }

                    // Android and iOS require coordinate from stored Anchor
#if UNITY_ANDROID || UNITY_IOS
                    var detectedAnchorPose = detectedAnchors[anchorGuid].GetPose();
#else
                    var detectedAnchorPose = Pose.identity;
#endif

                    var anchoredObject = new GameObject($"Anchor - [{anchorGuid}]");
                    anchoredObject.transform.SetPositionAndRotation(detectedAnchorPose.position, detectedAnchorPose.rotation);

                    var attachedAnchor = anchoredObject.EnsureComponent<CloudNativeAnchor>();
                    attachedAnchor.CloudToNative(detectedAnchors[anchorGuid]);

                    OnAnchorLocated(anchorGuid, anchoredObject);
                }
                else
                {
                    var errorMessage = $"Anchor returned from service but Identifier was invalid [{args.Identifier}]";
                    OnSpatialPersistenceError(errorMessage);
                }
            }
            catch (Exception ex)
            {
                if (Guid.TryParse(args.Identifier, out var anchorGuid))
                {
                    var errorMessage = $"An anchor [{anchorGuid}] was returned with invalid data\nError reported as {ex}";
                    OnAnchorLocatedError(anchorGuid, errorMessage);
                }
                else
                {
                    var errorMessage = $"An Error Occurred retrieving the Anchor, Anchor ignored\n{ex}";
                    OnSpatialPersistenceError(errorMessage);
                }
            }
        }
        #endregion Private Methods
    }
}