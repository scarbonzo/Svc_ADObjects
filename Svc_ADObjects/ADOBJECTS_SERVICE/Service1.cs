using ADOBJECTS_DATA;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Entity.Infrastructure;
using System.Diagnostics;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.DirectoryServices;
using System.DirectoryServices.AccountManagement;
using System.Collections;

namespace ADOBJECTS_SERVICE
{
    public partial class Service1 : ServiceBase
    {
        //The main timer
        private System.Timers.Timer m_mainTimer;
        //How often to scan Active Directory in milliseconds (minutes * 60000)
        private int scanInterval = 30 * 60000; 
        //Entity hook
        private ActiveDirectoryObjectsEntities db = new ActiveDirectoryObjectsEntities();

        public Service1()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            //Create the Main timer
            m_mainTimer = new System.Timers.Timer();
            //Set the timer interval
            m_mainTimer.Interval = scanInterval;
            //Dictate what to do when the event fires
            m_mainTimer.Elapsed += m_mainTimer_Elapsed;
            //Something to do with something, I forgot since it's been a while
            m_mainTimer.AutoReset = true;

#if DEBUG
#else
            m_mainTimer.Start(); //Start timer only in Release
#endif
            //Run 1st Tick Manually
            UpdateUsers();
        }

        public void OnDebug()
        {
            //Manually kick off the service when debugging
            OnStart(null);
        }

        protected override void OnStop()
        {
        }

        void m_mainTimer_Elapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            //Each interval run the UpdateUsers() function
            UpdateUsers();
        }

        private void UpdateUsers()
        {
            //Get a List<> of user accounts in the database for cleaning routine later
            List<ActiveDirectoryUser> InactiveUsers = GetCurrentDbUsers();

            //Fetch all of the current users and update the database
            foreach (DirectoryEntry de in GetCurrentADUsers())
            {
                //Extract the username from the DirectoryEntry
                string username = de.Properties["samAccountName"].Value.ToString();

                //See if the user exists in the database
                var existingUser = CheckExistingActiveDBUser(username);

                if (existingUser != null)
                {
                    //If the user exists, use the existing guid to update the properties
                    db.Entry(existingUser).CurrentValues.SetValues(CreateUserFromDE(de, existingUser.Id));
                }
                else
                {
                    //If the user does not exist, or is not active, create a new user object
                    try
                    {
                        db.ActiveDirectoryUsers.Add(CreateUserFromDE(de));
                    }
                    catch { }
                }

                //Save the user object to the database
                db.SaveChanges();

                //Remove the use from the InactiveUsers List<>
                var user = InactiveUsers.Find(u => u.username == username);
                InactiveUsers.Remove(user);
            }

            //Cleaning Routine
            //Fetch all of the users left in the InactiveUsers List<> and iterate through them
            foreach (ActiveDirectoryUser user in InactiveUsers)
            {
                //Attach to the current user
                db.ActiveDirectoryUsers.Attach(user);
                //Mark them as inactive
                db.Entry(user).Property(u => u.active).CurrentValue = false;
                //Save the changes to the db (probably want to call this outside of the foreach...)
                db.SaveChanges();
            }
        }

        //Active Directory Functions
        //Returns a List<> of DirectoryEntries for all current AD users
        public List<DirectoryEntry> GetCurrentADUsers()
        {
            List<DirectoryEntry> entries = new List<DirectoryEntry>();

            using (var context = new PrincipalContext(ContextType.Domain, "lsnj.org"))
            {
                using (var searcher = new PrincipalSearcher(new UserPrincipal(context)))
                {
                    UserPrincipal user = new UserPrincipal(context);
                    user.Enabled = true; //Only search for enabled accounts
                    searcher.QueryFilter = user;

                    foreach (var result in searcher.FindAll())
                    {
                        entries.Add(result.GetUnderlyingObject() as DirectoryEntry);
                    }
                }
            }

            return entries;
        }

        //Do some hocus pocus on strange Microsoft numbers
        private Int64 GetInt64(DirectoryEntry de, string attr)
        {
            DirectorySearcher ds = new DirectorySearcher(
                de,
                String.Format("({0}=*)", attr),
                new string[] { attr },
                SearchScope.Base
                );

            SearchResult sr = ds.FindOne();

            if (sr != null)
            {
                if (sr.Properties.Contains(attr))
                {
                    return (Int64)sr.Properties[attr][0];
                }
            }
            return -1;
        }

        //Database Functions
        //Create an ActiveDirectoryUser object from a DirectoryEntry
        public ActiveDirectoryUser CreateUserFromDE(DirectoryEntry de, Guid? Id = null)
        {
            ActiveDirectoryUser user = new ActiveDirectoryUser();

            if (Id != null)
            {
                user.Id = (Guid)Id;
            }
            else
            {
                user.Id = Guid.NewGuid();
            }
            try
            {
                user.username = de.Properties["samAccountName"].Value.ToString();
            }
            catch { }
            try
            {
                user.ou = de.Properties["distinguishedName"].Value.ToString();
            }
            catch { }
            try
            {
                user.displayname = de.Properties["displayName"].Value.ToString();
            }
            catch { }
            try
            {
                user.firstname = de.Properties["givenName"].Value.ToString();
            }
            catch { }
            try
            {
                user.lastname = de.Properties["sn"].Value.ToString();
            }
            catch { }
            try
            {
                user.passwordlastset = DateTime.FromFileTime(GetInt64(de, "pwdLastSet")); //If valid, update the Last Set Date
                if (user.passwordlastset < DateTime.Now.AddYears(-100))
                    user.passwordlastset = null;
            }
            catch { }
            try
            {
                user.lastlogin = DateTime.FromFileTime(GetInt64(de, "lastLogon")); //If valid, update the Last Login Date
                if (user.lastlogin < DateTime.Now.AddYears(-100))
                    user.lastlogin = null;
            }
            catch { }
            try
            {
                user.program = de.Properties["company"].Value.ToString();
            }
            catch { }
            try
            {
                user.office = de.Properties["physicalDeliveryOfficeName"].Value.ToString();
            }
            catch { }
            try
            {
                user.email = de.Properties["mail"].Value.ToString();
            }
            catch { }
            try
            {
                user.extension = de.Properties["telephoneNumber"].Value.ToString();
            }
            catch { }
            try
            {
                var memberGroups = de.Properties["memberOf"].Value;

                if (memberGroups.GetType() == typeof(string))
                {
                    user.groups = (string)memberGroups;
                }
                else if (memberGroups.GetType().IsArray)
                {
                    var memberGroupsEnumerable = memberGroups as IEnumerable;

                    if (memberGroupsEnumerable != null)
                    {
                        var asStringEnumerable = memberGroupsEnumerable.OfType<object>().Select(obj => obj.ToString());
                        user.groups = string.Join(", ", asStringEnumerable);
                    }
                }

                List<string> _groups = new List<string>();

                foreach (string group in user.groups.Split(',').ToList().Where(g => g.Contains("CN=")).ToList())
                {
                    _groups.Add(group.Replace("CN=", "").Trim());
                }

                user.groups = string.Join(",", _groups);
            }
            catch { }
            try
            {
                user.title = de.Properties["title"].Value.ToString();
            }
            catch { }
            try
            {
                if (Convert.ToBoolean((int)de.Properties["userAccountControl"].Value & 65536))
                    user.expirable = false;
            }
            catch { }
            try
            {
                user.notes = de.Properties["info"].Value.ToString();
            }
            catch { }

            user.active = true;
            user.lastupdate = DateTime.Now;

            return user;
        }

        //Returns a List<> of users in the database
        public List<ActiveDirectoryUser> GetCurrentDbUsers()
        {
            return db.ActiveDirectoryUsers
                .Where(u => u.active == true)
                .ToList();
        }

        //Check to see if a username is already in the database (and active)
        public ActiveDirectoryUser CheckExistingActiveDBUser(string Username)
        {
            return db.ActiveDirectoryUsers
                .Where(u => u.username == Username)
                .Where(u => u.active == true)
                .FirstOrDefault();
        }
    }
}
