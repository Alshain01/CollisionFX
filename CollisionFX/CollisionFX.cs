using System;
using UnityEngine;
using ModuleWheels;

/**********************************************************
 * TODO:
 * Update cfg to support Kerbal Foundaries renamed wheels.
 * Fix sparks when kerbals walk on a craft.
 * Add particle colours for every body.
 * Tone down lighting.
 * Add more public cfg options.
 * 
 * Possible features:
 * Wheel skid sounds and smoke: WheelHit.(forward/side)Slip
 * Investigate using particle stretching to fake vacuum \
 * body dust jets for engines.
 **********************************************************/

namespace CollisionFX
{
    public class CollisionFX : PartModule
    {
        public static string ConfigPath = "GameData/CollisionFX/settings.cfg";

        [KSPField]
        public float volume = 0.5f;
        [KSPField]
        public bool scrapeSparks = true;
        [KSPField]
        public string collisionSound = String.Empty;
        [KSPField]
        public string wheelImpactSound = String.Empty;
        [KSPField]
        public string scrapeSound = String.Empty;
        [KSPField]
        public string sparkSound = String.Empty;
        [KSPField]
        public float sparkLightIntensity = 0.05f;
        [KSPField]
        public float minScrapeSpeed = 1f;

        public float pitchRange = 0.3f;
        public float scrapeFadeSpeed = 5f;

        private GameObject sparkFx;
        private GameObject dustFx;
        ParticleAnimator dustAnimator;
        //private GameObject fragmentFx;
        //ParticleAnimator fragmentAnimator;
        private ModuleWheelBase moduleWheel = null;
        private ModuleWheelDamage moduleWheelDamage = null;
        private WheelCollider wheelCollider = null;
        private FXGroup ScrapeSounds = new FXGroup("ScrapeSounds");
        private FXGroup SparkSounds = new FXGroup("SparkSounds");
        private FXGroup BangSound = new FXGroup("BangSound");
        private FXGroup WheelImpactSound = null;
        private Light scrapeLight;
        private Color lightColor1 = new Color(254, 226, 160); // Tan / light orange
        private Color lightColor2 = new Color(239, 117, 5); // Red-orange.

#if DEBUG
        private GameObject[] spheres = new GameObject[3];
#endif

        public class CollisionInfo
        {
            public CollisionFX CollisionFX;
            public bool IsWheel;

            public CollisionInfo(CollisionFX collisionFX, bool isWheel)
            {
                CollisionFX = collisionFX;
                IsWheel = isWheel;
            }
        }

        #region Events

        public override void OnStart(StartState state)
        {
            if (state == StartState.Editor || state == StartState.None) return;

            SetupParticles();
            if (scrapeSparks)
                SetupLight();

            if (part.Modules.Contains("ModuleWheelBase")) // Suppress the log message on failure.
                moduleWheel = part.Modules["ModuleWheelBase"] as ModuleWheelBase;
            if (part.Modules.Contains("ModuleWheelDamage"))
                moduleWheelDamage = part.Modules["ModuleWheelDamage"] as ModuleWheelDamage;
            wheelCollider = part.GetComponent<WheelCollider>();
            // No longer necessary, all wheel parts and landing gear parts are the same now - Alshain01
            /*if (wheelCollider == null)
            {
                ModuleLandingGear gear = part.GetComponent<ModuleLandingGear>();
                if (gear != null)
                {
                    wheelCollider = gear.wheelCollider;
                }
            }*/

            SetupAudio();

            GameEvents.onGamePause.Add(OnPause);
            GameEvents.onGameUnpause.Add(OnUnpause);

#if DEBUG
            for (int i = 0; i < spheres.Length; i++)
            {
                spheres[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                Destroy(spheres[i].GetComponent<Collider>());
            }
            spheres[0].GetComponent<Renderer>().material.color = Color.red;
            spheres[1].GetComponent<Renderer>().material.color = Color.green;
#endif

        }

        private bool _paused = false;
        private void OnPause()
        {
            _paused = true;
            if (SparkSounds != null && SparkSounds.audio != null)
                SparkSounds.audio.Stop();
        }

        private void OnUnpause()
        {
            _paused = false;
        }

        private void OnDestroy()
        {
            if (ScrapeSounds != null && ScrapeSounds.audio != null)
                ScrapeSounds.audio.Stop();
            if (SparkSounds != null && SparkSounds.audio != null)
                SparkSounds.audio.Stop();
            GameEvents.onGamePause.Remove(OnPause);
            GameEvents.onGameUnpause.Remove(OnUnpause);
        }

        // Not called on parts where physicalSignificance = false. Check the parent part instead.
        public void OnCollisionEnter(Collision c)
        {
            if (_paused) return;
            if (c.relativeVelocity.magnitude > 3)
            {
                if (c.contacts.Length == 0)
                    return;

                var cInfo = GetClosestChild(part, c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime));
                if (cInfo.CollisionFX != null)
                {
                    cInfo.CollisionFX.Impact(cInfo.IsWheel);
                }
                else
                {
                    Impact(IsCollidingWheel(c.contacts[0].point));
                }
            }
        }

        // Not called on parts where physicalSignificance = false. Check the parent part instead.
        public void OnCollisionStay(Collision c)
        {
            //DebugParticles(c.collider, c.contacts[0].point);

            if (!scrapeSparks || _paused) return;

            // Contact points are from the previous frame. Add the velocity to get the correct position.
            var cInfo = GetClosestChild(part, c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime));
            if (cInfo.CollisionFX != null)
            {
                StopScrapeLightSound();
                foreach (var p in part.children)
                {
                    var colFx = p.GetComponent<CollisionFX>();
                    if (colFx != null)
                        colFx.StopScrapeLightSound();
                }
                cInfo.CollisionFX.Scrape(c);
                return;
            }

            Scrape(c);
        }

        private void OnCollisionExit(Collision c)
        {
            StopScrapeLightSound();
            if (c.contacts.Length > 0)
            {
                var cInfo = GetClosestChild(part, c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime));
                if (cInfo.CollisionFX != null)
                    cInfo.CollisionFX.StopScrapeLightSound();
            }
        }

        #endregion Events

        /// <summary>
        /// This part has come into contact with something. Play an appropriate sound.
        /// </summary>
        public void Impact(bool isWheel)
        {
            if (isWheel && WheelImpactSound != null && WheelImpactSound.audio != null)
            {
                WheelImpactSound.audio.pitch = UnityEngine.Random.Range(1 - pitchRange, 1 + pitchRange);
                WheelImpactSound.audio.Play();
                if (BangSound != null && BangSound.audio != null)
                {
                    BangSound.audio.Stop();
                }
            }
            else
            {
                if (BangSound != null && BangSound.audio != null)
                {
                    // Shift the pitch randomly each time so that the impacts don't all sound the same.
                    BangSound.audio.pitch = UnityEngine.Random.Range(1 - pitchRange, 1 + pitchRange);
                    BangSound.audio.Play();
                }
                if (WheelImpactSound != null && WheelImpactSound.audio != null)
                {
                    WheelImpactSound.audio.Stop();
                }
            }
        }

        /*private string[] particleTypes = {
                                            "fx_exhaustFlame_white_tiny",
                                            "fx_exhaustFlame_yellow",
                                            "fx_exhaustFlame_blue",
                                            //"fx_exhaustLight_yellow",
                                            "fx_exhaustLight_blue",
                                            "fx_exhaustFlame_blue_small",
                                            "fx_smokeTrail_light",
                                            "fx_smokeTrail_medium",
                                            "fx_smokeTrail_large",
                                            "fx_smokeTrail_veryLarge",
                                            "fx_smokeTrail_aeroSpike",
                                            "fx_gasBurst_white",
                                            "fx_gasJet_white",
                                            "fx_SRB_large_emit",
                                            "fx_SRB_large_emit2",
                                            "fx_exhaustSparks_flameout",
                                            "fx_exhaustSparks_flameout_2",
                                            "fx_exhaustSparks_yellow",
                                            "fx_shockExhaust_red_small", nope
                                            "fx_shockExhaust_blue_small",
                                            "fx_shockExhaust_blue",
                                            "fx_LES_emit",
                                            "fx_ksX_emit",
                                            "fx_ks25_emit",
                                            "fx_ks1_emit"
                                         };*/

            //int currentParticle = 0;
        private void SetupParticles()
        {
            /*UnityEngine.Object o = null;
            while (o == null)
            {
                string name = "Effects/" + particleTypes[currentParticle];
                Debug.Log("Attempting to load " + name);
                o = UnityEngine.Resources.Load(name);
                currentParticle++;
                if (currentParticle >= particleTypes.Length) currentParticle = 0;
            }*/

            //ScreenMessages.PostScreenMessage(particleTypes[currentParticle]);
            if (scrapeSparks)
            {
                sparkFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_exhaustSparks_flameout"));
                sparkFx.transform.parent = part.transform;
                sparkFx.transform.position = part.transform.position;
                sparkFx.GetComponent<ParticleEmitter>().localVelocity = Vector3.zero;
                sparkFx.GetComponent<ParticleEmitter>().useWorldSpace = true;
                sparkFx.GetComponent<ParticleEmitter>().emit = false;
                sparkFx.GetComponent<ParticleEmitter>().minEnergy = 0;
                sparkFx.GetComponent<ParticleEmitter>().minEmission = 0;
            }

            dustFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_smokeTrail_light"));
            dustFx.transform.parent = part.transform;
            dustFx.transform.position = part.transform.position;
            dustFx.GetComponent<ParticleEmitter>().localVelocity = Vector3.zero;
            dustFx.GetComponent<ParticleEmitter>().useWorldSpace = true;
            dustFx.GetComponent<ParticleEmitter>().emit = false;
            dustFx.GetComponent<ParticleEmitter>().minEnergy = 0;
            dustFx.GetComponent<ParticleEmitter>().minEmission = 0;
            dustAnimator = dustFx.GetComponent<ParticleEmitter>().GetComponent<ParticleAnimator>();

            /*fragmentFx = (GameObject)GameObject.Instantiate(UnityEngine.Resources.Load("Effects/fx_exhaustSparks_yellow"));
            fragmentFx.transform.parent = part.transform;
            fragmentFx.transform.position = part.transform.position;
            fragmentFx.particleEmitter.localVelocity = Vector3.zero;
            fragmentFx.particleEmitter.useWorldSpace = true;
            fragmentFx.particleEmitter.emit = false;
            fragmentFx.particleEmitter.minEnergy = 0;
            fragmentFx.particleEmitter.minEmission = 0;
            fragmentAnimator = fragmentFx.particleEmitter.GetComponent<ParticleAnimator>();*/
        }

        private void SetupLight()
        {
            scrapeLight = sparkFx.AddComponent<Light>();
            scrapeLight.type = LightType.Point;
            scrapeLight.range = 3f;
            scrapeLight.shadows = LightShadows.None;
            scrapeLight.enabled = false;
        }

        private void SetupAudio()
        {
            if (scrapeSparks)
            {
                if (SparkSounds == null)
                {
                    Debug.LogError("CollisionFX: SparkSounds was null");
                    return;
                }
                if (!String.IsNullOrEmpty(sparkSound))
                {
                    part.fxGroups.Add(SparkSounds);
                    SparkSounds.name = "SparkSounds";
                    SparkSounds.audio = gameObject.AddComponent<AudioSource>();
                    SparkSounds.audio.clip = GameDatabase.Instance.GetAudioClip(sparkSound);
                    if (SparkSounds.audio.clip == null)
                    {
                        Debug.LogError("CollisionFX: Unable to load sparkSound \"" + sparkSound + "\"");
                        scrapeSparks = false;
                        return;
                    }
                    SparkSounds.audio.dopplerLevel = 0f;
                    SparkSounds.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    SparkSounds.audio.Stop();
                    SparkSounds.audio.loop = true;
                    SparkSounds.audio.volume = volume * GameSettings.SHIP_VOLUME;
                    SparkSounds.audio.time = UnityEngine.Random.Range(0, SparkSounds.audio.clip.length);
                }
            }

            if (ScrapeSounds == null)
            {
                Debug.LogError("CollisionFX: ScrapeSounds was null");
                return;
            }
            if (!String.IsNullOrEmpty(scrapeSound))
            {
                part.fxGroups.Add(ScrapeSounds);
                ScrapeSounds.name = "ScrapeSounds";
                ScrapeSounds.audio = gameObject.AddComponent<AudioSource>();
                ScrapeSounds.audio.clip = GameDatabase.Instance.GetAudioClip(scrapeSound);
                if (ScrapeSounds.audio.clip == null)
                {
                    Debug.LogError("CollisionFX: Unable to load scrapeSound \"" + scrapeSound + "\"");
                }
                else
                {
                    ScrapeSounds.audio.dopplerLevel = 0f;
                    ScrapeSounds.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                    ScrapeSounds.audio.Stop();
                    ScrapeSounds.audio.loop = true;
                    ScrapeSounds.audio.volume = volume * GameSettings.SHIP_VOLUME;
                    ScrapeSounds.audio.time = UnityEngine.Random.Range(0, ScrapeSounds.audio.clip.length);
                }
            }

            if (!String.IsNullOrEmpty(collisionSound))
            {
                part.fxGroups.Add(BangSound);
                BangSound.name = "BangSound";
                BangSound.audio = gameObject.AddComponent<AudioSource>();
                BangSound.audio.clip = GameDatabase.Instance.GetAudioClip(collisionSound);
                BangSound.audio.dopplerLevel = 0f;
                BangSound.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                BangSound.audio.Stop();
                BangSound.audio.loop = false;
                BangSound.audio.volume = GameSettings.SHIP_VOLUME;
            }

            if (wheelCollider != null && !String.IsNullOrEmpty(wheelImpactSound))
            {
                WheelImpactSound = new FXGroup("WheelImpactSound");
                part.fxGroups.Add(WheelImpactSound);
                WheelImpactSound.name = "WheelImpactSound";
                WheelImpactSound.audio = gameObject.AddComponent<AudioSource>();
                WheelImpactSound.audio.clip = GameDatabase.Instance.GetAudioClip(wheelImpactSound);
                WheelImpactSound.audio.dopplerLevel = 0f;
                WheelImpactSound.audio.rolloffMode = AudioRolloffMode.Logarithmic;
                WheelImpactSound.audio.Stop();
                WheelImpactSound.audio.loop = false;
                WheelImpactSound.audio.volume = GameSettings.SHIP_VOLUME;
            }
        }

        public void DebugParticles(Collider col, Vector3 contactPoint)
        {
            Color c = ColourManager.GetBiomeColour(col);
            dustFx.transform.position = contactPoint;
            dustFx.GetComponent<ParticleEmitter>().maxEnergy = 10;
            dustFx.GetComponent<ParticleEmitter>().maxEmission = 75;
            dustFx.GetComponent<ParticleEmitter>().Emit();
            //dustFx.particleEmitter.worldVelocity = -part.rigidbody.velocity;
            // Set dust biome colour.
            if (dustAnimator != null)
            {
                Color[] colors = dustAnimator.colorAnimation;
                colors[0] = c;
                colors[1] = c;
                colors[2] = c;
                colors[3] = c;
                colors[4] = c;
                dustAnimator.colorAnimation = colors;
            }
        }

        /// <summary>
        /// Checks whether the collision is happening on this part's wheel.
        /// </summary>
        /// <returns></returns>
        private bool IsCollidingWheel(Vector3 collisionPoint)
        {
            if (wheelCollider == null) return false;
            float wheelDistance = Vector3.Distance(wheelCollider.ClosestPointOnBounds(collisionPoint), collisionPoint);
            float partDistance = Vector3.Distance(part.collider.ClosestPointOnBounds(collisionPoint), collisionPoint);
            return wheelDistance < partDistance;
        }

        /// <summary>
        /// Searches child parts for the nearest instance of this class to the given point.
        /// </summary>
        /// <remarks>Parts with physicalSignificance=false have their collisions detected by the parent part.
        /// To identify which part is the source of a collision, check which part the collision is closest to.</remarks>
        /// <param name="parent">The parent part whose children should be tested.</param>
        /// <param name="p">The point to test the distance from.</param>
        /// <returns>The nearest child part's CollisionFX module, or null if the parent part is nearest.</returns>
        private static CollisionInfo GetClosestChild(Part parent, Vector3 p)
        {
            float parentDistance = Vector3.Distance(parent.transform.position, p);
            float minDistance = parentDistance;
            CollisionFX closestChild = null;
            bool isWheel = false;

            foreach (Part child in parent.children)
            {
                if (child != null && child.collider != null &&
                    (child.physicalSignificance == Part.PhysicalSignificance.NONE))
                {
                    float childDistance = Vector3.Distance(child.transform.position, p);
                    var cfx = child.GetComponent<CollisionFX>();
                    if (cfx != null)
                    {
                        if (cfx.wheelCollider != null)
                        {
                            float wheelDistance = Vector3.Distance(cfx.wheelCollider.ClosestPointOnBounds(p), p);
                            if (wheelDistance < childDistance)
                            {
                                isWheel = true;
                                childDistance = wheelDistance;
                            }
                        }

                        if (childDistance < minDistance)
                        {
                            minDistance = childDistance;
                            closestChild = cfx;
                        }
                        else
                            isWheel = false;
                    }
                    else
                        isWheel = false;
                }
            }
            return new CollisionInfo(closestChild, isWheel);
        }

        /// <summary>
        /// This part is moving against another surface. Create sound, light and particle effects.
        /// </summary>
        /// <param name="c"></param>
        public void Scrape(Collision c)
        {
            if (_paused || part == null)
            {
                StopScrapeLightSound();
                return;
            }
            float m = c.relativeVelocity.magnitude;
            if (wheelCollider != null)
            {

                // Has a wheel collider.
                if (moduleWheel != null)
                {
                    // Has a wheel module.
                    if (moduleWheelDamage != null && !moduleWheelDamage.isDamaged)
                    {
                        // Has an intact wheel.
                        StopScrapeLightSound();
                        ScrapeParticles(m, c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime), c.collider, c.gameObject);
                        return;
                    }
                }
                else
                {
                    // Has a wheel collider but not a wheel (hover parts, some landing gear).
                    StopScrapeLightSound();
                    ScrapeParticles(m, c.contacts[0].point + (part.vel * Time.deltaTime), c.collider, c.gameObject);
                    return;
                }
            }

            if (sparkFx != null)
                sparkFx.transform.LookAt(c.transform);
            if (part.Rigidbody == null) // Part destroyed?
            {
                StopScrapeLightSound();
                return;
            }

            ScrapeParticles(m, c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime), c.collider, c.gameObject);
            ScrapeSound(ScrapeSounds, m);
            if (CanSpark(c.collider, c.gameObject))
                ScrapeSound(SparkSounds, m);
            else
            {
                if (SparkSounds != null && SparkSounds.audio != null)
                    SparkSounds.audio.Stop();
            }

#if DEBUG
            spheres[0].GetComponent<Renderer>().enabled = false;
            spheres[1].GetComponent<Renderer>().enabled = true;
            spheres[1].transform.position = part.transform.position;
            spheres[2].transform.position = c.contacts[0].point + (part.Rigidbody.velocity * Time.deltaTime);
#endif
        }

        public void StopScrapeLightSound()
        {
            if (scrapeSparks)
            {
                if (SparkSounds != null && SparkSounds.audio != null)
                    SparkSounds.audio.Stop();
#if DEBUG
                Debug.Log("#Stopping scrape sparks");
                spheres[0].transform.position = part.transform.position;
                spheres[0].GetComponent<Renderer>().enabled = true;
                spheres[1].GetComponent<Renderer>().enabled = false;
#endif
                scrapeLight.enabled = false;
            }

            if (ScrapeSounds != null && ScrapeSounds.audio != null)
                ScrapeSounds.audio.Stop();
        }

        /* Kerbin natural biomes:
            Water           Sand. If we're hitting a terrain collider here, it will be sandy.
            Grasslands      Dirt
            Highlands       Dirt (dark?)
            Shores          Dirt (too grassy for sand)
            Mountains       Rock particles?
            Deserts         Sand
            Badlands        ?
            Tundra          Dirt?
            Ice Caps        Snow
         */

        public bool HasIntactWheel()
        {
            if (wheelCollider == null)
                return false;
            // Has a wheel collider.

            if (moduleWheel == null)
                return false;
            // Has a wheel module.

            if (moduleWheelDamage == null)
                return true;

            // Has an intact wheel.
            return !moduleWheelDamage.isDamaged;
        }

        public bool IsRagdoll(GameObject g)
        {
            /*
            be_neck01
            be_spE01
            bn_l_arm01 1
            bn_l_elbow_a01
            bn_l_hip01
            bn_l_knee_b01
            bn_r_elbow_a01
            bn_r_hip01
            bn_r_knee_b01
            bn_spA01
            */
            // TODO: Find a better way of doing this.
            return g.name.StartsWith("bn_") || g.name.StartsWith("be_");
        }

        /// <summary>
        /// Whether the object collided with produces sparks.
        /// </summary>
        /// <returns>True if the object is a CollisionFX with scrapeSparks or a non-CollisionFX object.</returns>
        public bool TargetScrapeSparks(GameObject collidedWith)
        {
            CollisionFX objectCollidedWith = collidedWith.GetComponent<CollisionFX>();
            return objectCollidedWith == null ? true : objectCollidedWith.scrapeSparks;
        }

        public bool CanSpark(Collider c, GameObject collidedWith)
        {
            return scrapeSparks && TargetScrapeSparks(collidedWith) && FlightGlobals.ActiveVessel.atmDensity > 0 &&
                FlightGlobals.currentMainBody.atmosphereContainsOxygen && !ColourManager.IsPQS(c);
        }

        private void ScrapeParticles(float speed, Vector3 contactPoint, Collider col, GameObject collidedWith)
        {
            if (speed > minScrapeSpeed)
            {
                if (!ColourManager.IsPQS(col) && TargetScrapeSparks(collidedWith) && !IsRagdoll(collidedWith))
                {
                    if (CanSpark(col, collidedWith) && FlightGlobals.ActiveVessel.atmDensity > 0 && FlightGlobals.currentMainBody.atmosphereContainsOxygen)
                    {
                        sparkFx.transform.position = contactPoint;
                        sparkFx.GetComponent<ParticleEmitter>().maxEnergy = speed / 10;                          // Values determined
                        sparkFx.GetComponent<ParticleEmitter>().maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                        sparkFx.GetComponent<ParticleEmitter>().Emit();
                        sparkFx.GetComponent<ParticleEmitter>().worldVelocity = -part.Rigidbody.velocity;
                        scrapeLight.enabled = true;
                        scrapeLight.color = Color.Lerp(lightColor1, lightColor2, UnityEngine.Random.Range(0f, 1f));
                        float intensityMultiplier = 1;
                        if (speed < minScrapeSpeed * 10)
                            intensityMultiplier = speed / (minScrapeSpeed * 10);
                        scrapeLight.intensity = UnityEngine.Random.Range(0f, sparkLightIntensity * intensityMultiplier);
                    }
                    else
                    {
                        /*fragmentFx.transform.position = contactPoint;
                        fragmentFx.particleEmitter.maxEnergy = speed / 10;                          // Values determined
                        fragmentFx.particleEmitter.maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                        fragmentFx.particleEmitter.Emit();
                        fragmentFx.particleEmitter.worldVelocity = -part.rigidbody.velocity;
                        if (fragmentAnimator != null)
                        {
                            Color[] colors = dustAnimator.colorAnimation;
                            Color light = new Color(0.95f, 0.95f, 0.95f);
                            Color dark = new Color(0.05f, 0.05f, 0.05f);

                            colors[0] = Color.gray;
                            colors[1] = light;
                            colors[2] = dark;
                            colors[3] = light;
                            colors[4] = dark;
                            fragmentAnimator.colorAnimation = colors;
                        }*/
                    }
                }

                if (!wheelCollider)
                {
                    Color c = ColourManager.GetBiomeColour(col);
                    dustFx.transform.position = contactPoint;
                    dustFx.GetComponent<ParticleEmitter>().maxEnergy = speed / 10;                          // Values determined
                    dustFx.GetComponent<ParticleEmitter>().maxEmission = Mathf.Clamp((speed * 2), 0, 75);   // via experimentation.
                    dustFx.GetComponent<ParticleEmitter>().Emit();
                    //dustFx.particleEmitter.worldVelocity = -part.rigidbody.velocity;
                    // Set dust biome colour.
                    if (dustAnimator != null)
                    {
                        Color[] colors = dustAnimator.colorAnimation;
                        colors[0] = c;
                        colors[1] = c;
                        colors[2] = c;
                        colors[3] = c;
                        colors[4] = c;
                        dustAnimator.colorAnimation = colors;
                    }
                }
            }
            else
            {
                if (scrapeLight != null)
                    scrapeLight.enabled = false;
            }
        }

        private void ScrapeSound(FXGroup sound, float speed)
        {
            if (sound == null || sound.audio == null)
                return;
            if (speed > minScrapeSpeed)
            {
                if (!sound.audio.isPlaying)
                    sound.audio.Play();
                sound.audio.pitch = 1 + Mathf.Log(speed) / 5;

                if (speed < scrapeFadeSpeed)
                {
                    // Fade out at low speeds.
                    sound.audio.volume = speed / scrapeFadeSpeed * volume * GameSettings.SHIP_VOLUME;
                }
                else
                    sound.audio.volume = volume * GameSettings.SHIP_VOLUME;
            }
            else
                sound.audio.Stop();
        }
    }

#if DEBUG
    [KSPAddon(KSPAddon.Startup.MainMenu, false)]
    class AutoStartup : UnityEngine.MonoBehaviour
    {
        public static bool first = true;
        public void Start()
        {
            //only do it on the first entry to the menu
            if (first)
            {
                first = false;
                HighLogic.SaveFolder = "test";
                var game = GamePersistence.LoadGame("persistent", HighLogic.SaveFolder, true, false);
                if (game != null && game.flightState != null && game.compatible)
                    FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
                CheatOptions.InfinitePropellant = true;
            }
        }
    }
#endif
}


//ScreenMessages.PostScreenMessage("Collider: " + c.collider + "\ngameObject: " + c.gameObject + "\nrigidbody: " + c.rigidbody + "\ntransform: " + c.transform);
/*
    Collider		    gameObject		    rigidbody		transform
    runway_collider		runway_collider		""			runway_collider
    End09_Mesh		    End09_Mesh		    ""			End09_Mesh
    Section4_Mesh
    Section3_Mesh
    Section2_Mesh
    Section1_Mesh
    End27_Mesh
    Zn1232223233		Zn1232223233		""			Zn1232223233		
    Zn1232223332
    model_launchpad_ground_collider_v46
    Launch Pad
    launchpad_ramps
    Fuel Pipe
    Fuel Port
    launchpad_shoulders
    model_vab_exterior_crawlerway_collider_v46
    Zn1232223211
    Zn1232223210
    Zn1232223032
    Zn3001100000 - mountain top
    Zn2101022132 - Desert
    Zn2101022133
    Yp0333302322 - North pole
 * */