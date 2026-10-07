namespace AmaanParkingSystem.Models
{
    public class ModulePermission
    {
        public bool can_view { get; set; }
        public bool can_add { get; set; }
        public bool can_edit { get; set; }
        public bool can_delete { get; set; }
    }

    public class OperatorPermissionModel
    {
        public int operator_id { get; set; }
        public string operator_name { get; set; }
        public string operator_type { get; set; }
        public string operator_mobile { get; set; }
        public Dictionary<string, ModulePermission> modules { get; set; } = new();
    }

    public class AssignRightsListModel
    {
        public int id { get; set; }
        public string operator_type { get; set; }
        public string operator_login_id { get; set; }
        public string operator_name { get; set; }
        public string operator_mobile { get; set; }
        public string operator_permissions { get; set; }
        public string operator_status { get; set; }
        public bool has_permissions { get; set; }
        public int total_modules { get; set; }
        public int allowed_modules { get; set; }
    }
}
