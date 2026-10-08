import PropTypes from "prop-types";

// Success/error message under a form in a settings card
const SettingsStatus = ({ status }) =>
    status ? (
        <div className={`settings-alert ${status.type}`} role={status.type === "danger" ? "alert" : "status"}>
            {status.message}
        </div>
    ) : null;

SettingsStatus.propTypes = {
    status: PropTypes.shape({
        type: PropTypes.oneOf(["success", "danger"]).isRequired,
        message: PropTypes.string.isRequired,
    }),
};

export default SettingsStatus;
