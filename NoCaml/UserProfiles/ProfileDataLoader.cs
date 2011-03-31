﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Linq.Expressions;
using System.Reflection;

namespace NoCaml.UserProfiles
{
    public abstract class ProfileDataLoader<TProfile, TSource> : ProfileDataLoader<TProfile> where TProfile : ProfileBase
    {

        public Dictionary<Expression<Func<TProfile, object>>, Func<TProfile, TSource, object>> Mappings { get; set; }

        /// <summary>
        /// Mappings which will be executed even if the source is null
        /// </summary>
        public Dictionary<Expression<Func<TProfile, object>>, Func<TProfile, TSource, object>> NullableMappings { get; set; }

        protected ProfileDataLoader()
        {
            NullableMappings = new Dictionary<Expression<Func<TProfile, object>>, Func<TProfile, TSource, object>>();
            Mappings = new Dictionary<Expression<Func<TProfile, object>>, Func<TProfile, TSource, object>>();
        }



        public void UpdateProperty(TProfile profile, TSource source, Expression<Func<TProfile, object>> expr, Func<TProfile, TSource, object> val)
        {
            var n = expr.Body as MemberExpression;
            if (expr.Body.NodeType == ExpressionType.Convert) n = ((UnaryExpression)expr.Body).Operand as MemberExpression;

            var pi = n.Member as PropertyInfo;

            //    Debug.WriteLine(pi.Name);
            //            var pial = pi.GetCustomAttributes(typeof(ProfilePropertySourceAttribute), false).Cast<ProfilePropertySourceAttribute>();
            var pial = pi.GetCustomAttributes(true).Where(a => a is ProfilePropertySourceAttribute).Cast<ProfilePropertySourceAttribute>();

            // foreach (var a in pial.OrderBy(aa => aa.Order))
            //  {
            //       Debug.WriteLine(a.ProfileSource);
            //  }

            var sa = pial.Where(a => a.ProfileSource == this.SourceName).FirstOrDefault();

            // if this loader is not a valid source, return
            if (sa == null) return;

            var currentsource = profile.GetCurrentSource(pi.Name);
            var nsa = pial.Where(a => a.ProfileSource == currentsource).FirstOrDefault();

            // if current source is higher priority, do not update
            if (nsa != null && nsa.Order < sa.Order && !sa.RaisePriorityIfChanged) return;

            var currentvalue = pi.GetValue(profile, null);
            var newvalue = val(profile, source);

            // if new source is lower priority but can raise priority on change, 
            // check stored hash, update if different

            if (sa.RaisePriorityIfChanged && newvalue as string != null)
            {
                var changed = profile.ImportedPropertyChanged(pi.Name, newvalue as string);
                if (nsa != null && nsa.Order < sa.Order && !changed) return;
            }


            // do not update if changed
            if (!sa.UseIfEmpty && (newvalue == null || string.IsNullOrEmpty(newvalue.ToString()))) return;

            // do not update if not changed
            if (currentvalue == newvalue
                || (currentvalue == null && newvalue is string && string.IsNullOrEmpty((string)newvalue))
                || (currentvalue != null && newvalue != null && currentvalue.ToString() == newvalue.ToString())
                )
                return;

            pi.SetValue(profile, newvalue, null);
            profile.SetUpdated(pi.Name, SourceName);

        }


        public void UpdateProfile(TProfile profile, TSource source)
        {

            foreach (var kv in NullableMappings)
            {
                UpdateProperty(profile, source, kv.Key, kv.Value);
            }

            if (source != null)
                foreach (var kv in Mappings)
                {
                    UpdateProperty(profile, source, kv.Key, kv.Value);
                }


            // for each field in mappings where shouldupdate is true

            // set value in profile
            // set isdirty and source

        }
    }

    public abstract class ProfileDataLoader<TProfile> where TProfile : ProfileBase
    {
        protected string SourceName { get; set; }


        /// <summary>
        /// Expiry time in seconds for real time updates. Default is 10 minutes.
        /// </summary>
        public virtual int RealTimeUpdateExpiry { get { return 600; } }

        /// <summary>
        /// Maximum number of profiles to update each time the update is run. Set to 0 if updates should not 
        /// happen in batch process.
        /// </summary>
        public virtual int SpreadUpdateProfileCount { get { return 0; } }


        /// <summary>
        /// recent update times used to skip full updates
        /// </summary>
        private static Dictionary<string, DateTime> InProcUpdates = new Dictionary<string, DateTime>();


        /// <summary>
        /// For sources where validity is dependent on other properties, allow filtering of the profile list 
        /// so that updates expected to fail do not run
        /// </summary>
        /// <param name="p"></param>
        /// <returns></returns>
        public virtual bool IsValidProfile(TProfile p)
        {
            return true;
        }


        /// <summary>
        /// Update an individual profile with no other data available - generally by calling a web service.        
        /// </summary>
        /// <param name="p"></param>
        protected virtual void UpdateProfileRealTime(TProfile p)
        {

        }

        /// <summary>
        /// Update an individual profile from data loaded with LoadBulkData
        /// </summary>
        /// <param name="p"></param>
        protected virtual void UpdateProfileBatch(TProfile p)
        {

        }


        /// <summary>
        /// For batch updates, load necessary data. Returns true if bulk data is available, false if
        /// individual updates are required.
        /// </summary>
        public bool LoadBulkData()
        {
            return false;
        }


        public static void RunRealtimeUpdate(IEnumerable<ProfileDataLoader<TProfile>> pdls, TProfile profile)
        {
            foreach (var pdl in pdls)
            {
                var k = pdl.SourceName + "|" + profile.LanID;
                if (!InProcUpdates.ContainsKey(k) || InProcUpdates[k] < DateTime.Now.AddSeconds(-pdl.RealTimeUpdateExpiry))
                {
                    pdl.UpdateProfileRealTime(profile);
                    InProcUpdates[k] = DateTime.Now;
                }
            }
        }


        public static void RunBatchUpdate(IEnumerable<ProfileDataLoader<TProfile>> pdls, IEnumerable<TProfile> profiles)
        {
            foreach (var pdl in pdls)
            {
                if (pdl.LoadBulkData())
                {
                    // bulk data was loaded, use it to update all
                    foreach (var p in profiles)
                    {
                        pdl.UpdateProfileBatch(p);
                    }
                }
                else
                {
                    // bulk data not available, run individual updates on a random selection of profiles
                    
                    // filter list and select random profiles
                    var rnd = new Random();
                    var fp = profiles
                        .Where(p => pdl.IsValidProfile(p))
                        .Where(p => !InProcUpdates.ContainsKey(pdl.SourceName + "|" + p.LanID) || InProcUpdates[pdl.SourceName + "|" + p.LanID] < DateTime.Now.AddSeconds(-pdl.RealTimeUpdateExpiry))
                        .OrderBy(p => rnd.Next())
                        .Take(pdl.SpreadUpdateProfileCount);

                    foreach (var p in fp)
                    {
                        pdl.UpdateProfileRealTime(p);
                        InProcUpdates[pdl.SourceName + "|" + p.LanID] = DateTime.Now; 
                    }
                }

            }
        }


    }
}
