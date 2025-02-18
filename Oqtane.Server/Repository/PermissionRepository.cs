using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oqtane.Models;
using Microsoft.Extensions.Caching.Memory;
using Oqtane.Infrastructure;

namespace Oqtane.Repository
{
    public class PermissionRepository : IPermissionRepository
    {
        private TenantDBContext _db;
        private readonly IRoleRepository _roles;
        private readonly IMemoryCache _cache;
        private readonly SiteState _siteState;

        public PermissionRepository(TenantDBContext context, IRoleRepository roles, IMemoryCache cache, SiteState siteState)
        {
            _db = context;
            _roles = roles;
            _cache = cache;
            _siteState = siteState;
         }

        public IEnumerable<Permission> GetPermissions(int siteId, string entityName)
        {
            var alias = _siteState?.Alias;
            if (alias != null)
            {
                return _cache.GetOrCreate($"permissions:{alias.TenantId}:{siteId}:{entityName}", entry =>
                {
                    entry.SlidingExpiration = TimeSpan.FromMinutes(30);
                    return _db.Permission.Where(item => item.SiteId == siteId)
                        .Where(item => item.EntityName == entityName)
                        .Include(item => item.Role).ToList(); // eager load roles
                });
            }
            return null;
        }

        public IEnumerable<Permission> GetPermissions(int siteId, string entityName, string permissionName)
        {
            var permissions = GetPermissions(siteId, entityName);
            return permissions.Where(item => item.PermissionName == permissionName);
        }

        public IEnumerable<Permission> GetPermissions(int siteId, string entityName, int entityId)
        {
            var permissions = GetPermissions(siteId, entityName);
            return permissions.Where(item => item.EntityId == entityId);
        }

        public IEnumerable<Permission> GetPermissions(int siteId, string entityName, int entityId, string permissionName)
        {
            var permissions = GetPermissions(siteId, entityName);
            return permissions.Where(item => item.EntityId == entityId)
                .Where(item => item.PermissionName == permissionName);
        }


        public Permission AddPermission(Permission permission)
        {
            _db.Permission.Add(permission);
            _db.SaveChanges();
            ClearCache(permission.SiteId, permission.EntityName);
            return permission;
        }

        public Permission UpdatePermission(Permission permission)
        {
            _db.Entry(permission).State = EntityState.Modified;
            _db.SaveChanges();
            ClearCache(permission.SiteId, permission.EntityName);
            return permission;
        }

        public void UpdatePermissions(int siteId, string entityName, int entityId, string permissionStrings)
        {
            bool modified = false;
            var existing = new List<Permission>();
            var permissions = DecodePermissions(permissionStrings, siteId, entityName, entityId);
            foreach (var permission in permissions)
            {
                if (!existing.Any(item => item.EntityName == permission.EntityName && item.PermissionName == permission.PermissionName))
                {
                    existing.AddRange(GetPermissions(siteId, permission.EntityName, permission.PermissionName)
                        .Where(item => item.EntityId == entityId || item.EntityId == -1));
                }

                var current = existing.FirstOrDefault(item => item.EntityName == permission.EntityName && item.EntityId == permission.EntityId
                    && item.PermissionName == permission.PermissionName && item.RoleId == permission.RoleId && item.UserId == permission.UserId);
                if (current != null)
                {
                    if (current.IsAuthorized != permission.IsAuthorized)
                    {
                        current.IsAuthorized = permission.IsAuthorized;
                        current.Role = null; // remove linked reference to Role which can cause errors in EF Core change tracking
                        _db.Entry(current).State = EntityState.Modified;
                        modified = true;
                    }
                }
                else
                {
                    _db.Permission.Add(permission);
                    modified = true;
                }
            }
            foreach (var permission in existing)
            {
                if (!permissions.Any(item => item.EntityName == permission.EntityName && item.PermissionName == permission.PermissionName
                    && item.EntityId == permission.EntityId && item.RoleId == permission.RoleId && item.UserId == permission.UserId))
                {
                    permission.Role = null; // remove linked reference to Role which can cause errors in EF Core change tracking
                    _db.Permission.Remove(permission);
                    modified = true;
                }
            }
            if (modified)
            {
                _db.SaveChanges();
                foreach (var entityname in permissions.Select(item => item.EntityName).Distinct())
                {
                    ClearCache(siteId, entityname);
                }
            }
        }

        public Permission GetPermission(int permissionId)
        {
            return _db.Permission.Find(permissionId);
        }

        public void DeletePermission(int permissionId)
        {
            Permission permission = _db.Permission.Find(permissionId);
            _db.Permission.Remove(permission);
            _db.SaveChanges();
            ClearCache(permission.SiteId, permission.EntityName);
        }

        public void DeletePermissions(int siteId, string entityName, int entityId)
        {
            IEnumerable<Permission> permissions = _db.Permission
                .Where(item => item.EntityName == entityName)
                .Where(item => item.EntityId == entityId)
                .Where(item => item.SiteId == siteId);
            foreach (Permission permission in permissions)
            {
                _db.Permission.Remove(permission);
            }
            _db.SaveChanges();
            ClearCache(siteId, entityName);
        }

        private void ClearCache(int siteId, string entityName)
        {
            var alias = _siteState?.Alias;
            if (alias != null)
            {
                _cache.Remove($"permissions:{alias.TenantId}:{siteId}:{entityName}");
            }
        }

        // permissions are stored in the format "{permissionname:!rolename1;![userid1];rolename2;rolename3;[userid2];[userid3]}" where "!" designates Deny permissions
        public string EncodePermissions(IEnumerable<Permission> permissionList)
        {
            List<PermissionString> permissionstrings = new List<PermissionString>();
            string permissionname = "";
            string permissions = "";
            StringBuilder permissionsbuilder = new StringBuilder();
            string securityid = "";
            foreach (Permission permission in permissionList.OrderBy(item => item.PermissionName))
            {
                // permission collections are grouped by permissionname
                if (permissionname != permission.PermissionName)
                {
                    permissions = permissionsbuilder.ToString();
                    if (permissions != "")
                    {
                        permissionstrings.Add(new PermissionString { PermissionName = permissionname, Permissions = permissions.Substring(0, permissions.Length - 1) });
                    }
                    permissionname = permission.PermissionName;
                    permissionsbuilder = new StringBuilder();
                }

                // deny permissions are prefixed with a "!"
                string prefix = !permission.IsAuthorized ? "!" : "";

                // encode permission
                if (permission.UserId == null)
                {
                    securityid = prefix + permission.Role.Name + ";";
                }
                else
                {
                    securityid = prefix + "[" + permission.UserId + "];";
                }

                // insert deny permissions at the beginning and append grant permissions at the end
                if (prefix == "!")
                {
                    permissionsbuilder.Insert(0, securityid);
                }
                else
                {
                    permissionsbuilder.Append(securityid);
                }
            }

            permissions = permissionsbuilder.ToString();
            if (permissions != "")
            {
                permissionstrings.Add(new PermissionString { PermissionName = permissionname, Permissions = permissions.Substring(0, permissions.Length - 1) });
            }
            return JsonSerializer.Serialize(permissionstrings);
        }

        public IEnumerable<Permission> DecodePermissions(string permissionStrings, int siteId, string entityName, int entityId)
        {
            List<Permission> permissions = new List<Permission>();
            List<Role> roles = _roles.GetRoles(siteId, true).ToList();
            string securityid = "";
            foreach (PermissionString permissionstring in JsonSerializer.Deserialize<List<PermissionString>>(permissionStrings))
            {
                foreach (string id in permissionstring.Permissions.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    securityid = id;
                    Permission permission = new Permission();
                    permission.SiteId = siteId;
                    if (!string.IsNullOrEmpty(permissionstring.EntityName))
                    {
                        permission.EntityName = permissionstring.EntityName;
                    }
                    else
                    {
                        permission.EntityName = entityName;
                    }
                    if (permission.EntityName == entityName)
                    {
                        permission.EntityId = entityId;
                    }
                    else
                    {
                        permission.EntityId = -1;
                    }
                    permission.PermissionName = permissionstring.PermissionName;
                    permission.RoleId = null;
                    permission.UserId = null;
                    permission.IsAuthorized = true;

                    if (securityid.StartsWith("!"))
                    {
                        // deny permission
                        securityid = securityid.Replace("!", "");
                        permission.IsAuthorized = false;
                    }
                    if (securityid.StartsWith("[") && securityid.EndsWith("]"))
                    {
                        // user id
                        securityid = securityid.Replace("[", "").Replace("]", "");
                        permission.UserId = int.Parse(securityid);
                    }
                    else
                    {
                        // role name
                        Role role = roles.SingleOrDefault(item => item.Name == securityid);
                        if (role != null)
                        {
                            permission.RoleId = role.RoleId;
                        }
                    }
                    if (permission.UserId != null || permission.RoleId != null)
                    {
                        permissions.Add(permission);
                    }
                }
            }
            return permissions;
        }
    }
}
