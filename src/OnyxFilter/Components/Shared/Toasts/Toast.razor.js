export const displayToast = (element, options) => {
    bootstrap.Toast.getOrCreateInstance(element, options).show();
};
